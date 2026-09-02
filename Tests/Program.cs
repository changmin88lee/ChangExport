using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using ChangExport.DwgProcessing;
using ChangExport.Export;
using ChangExport.Models;
using ChangExport.Standards;
using ChangExport.UI;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(@"C:\Program Files\Autodesk\Revit 2026", name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(ManagedDwgProcessor).Module.ModuleHandle);
        if (args.Length > 0 && args[0] == "compare-dwg")
        {
            try
            {
                PerformanceRegression.Compare(args[1], args[2], Check, normalizePeriodicAngles: true);
                Console.WriteLine($"PASS: {_checks} checks"); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        string output = Path.GetFullPath(args.Length > 1 ? args[1] : "bin/Verification/Tests");
        Directory.CreateDirectory(output);
        try
        {
            if (args.Length > 0 && args[0] == "dwg")
            { IndependentDwg(output, args[2]); CustomLayerRegression.Run(output, Check); RevitSheetRegression.Run(output, Check, Near); }
            else if (args.Length > 0 && args[0] == "custom") CustomLayerRegression.Run(output, Check);
            else if (args.Length > 0 && args[0] == "editable") { RevitSheetRegression.Run(output, Check, Near); EditableModelRegression.Run(output, Check, Near); }
            else if (args.Length > 0 && args[0] == "performance") { PerformanceRegression.Run(output, args[2], args[3], Check); PreparationQueueRegression.Run(args[2], Check); }
            else if (args.Length > 0 && args[0] == "compare-real") PerformanceRegression.CompareRuns(args[2], args[3], Check);
            else if (args.Length > 0 && args[0] == "layer-colors") LayerColorRegression.Run(output, args[2], args[3], Check);
            else if (args.Length > 0 && args[0] == "geometry") GeometryOptionsRegression.Run(output, Check, Near, args.Length > 2 ? args[2] : null);
            else if (args.Length > 0 && args[0] == "families") FamilyRecognitionRegression.Run(output, Check, args.Length > 2 ? args[2] : null);
            else if (args.Length > 0 && args[0] == "arcs") ArcRotationRegression.Run(output, Check, args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null);
            else if (args.Length > 0 && args[0] == "fills-real") FillAppearanceRegression.Run(output, args[2], Check);
            else if (args.Length > 0 && args[0] == "inspect") InspectDrawings(output, args[2]);
            else if (args.Length > 0 && args[0] == "real") ActualRevitDrawings(output, args[2]);
            else Managed(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = true, checks = _checks, mode = args.FirstOrDefault() ?? "managed", time = DateTimeOffset.Now }));
            Console.WriteLine($"PASS: {_checks} checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < 0.0001, $"{message}: {actual} != {expected}");

    private static void ActualRevitDrawings(string output, string workFolder)
    {
        var records = new List<object>(); var flats = new List<string>();
        var originals = Directory.GetFiles(workFolder, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        foreach (string path in Directory.GetFiles(workFolder, "sheet.dwg", SearchOption.AllDirectories))
        {
            string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            string target = Path.Combine(output, $"sheet-{records.Count + 1:000}.dwg");
            try
            {
                var result = new ManagedDwgProcessor().Run(new BridgeRequest { Operation = "Flatten", RevitSheet = true, OutputPath = target }, path, output);
                Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == before, "Original staging DWG unchanged");
                Check(result.Success && result.PaperEntityCount == 0, "Real sheet is in model space");
                flats.Add(target); records.Add(new { path, result });
                Console.WriteLine($"OK {records.Count}: {path}");
            }
            catch (Exception ex) { records.Add(new { path, error = ex.ToString() }); Console.WriteLine($"FAIL {path}: {ex.Message}"); }
        }
        File.WriteAllText(Path.Combine(output, "actual-files.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        Check(originals.All(pair => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "Every original DWG and PCP unchanged");
        Check(flats.Count == records.Count && flats.Count > 0, "Every actual Revit sheet converts");
        foreach (string direction in new[] { "Horizontal", "Vertical" })
        {
            var result = new ManagedDwgProcessor().Run(new BridgeRequest { Operation = "Merge", RevitSheet = true, Inputs = flats, Direction = direction, MarginMm = 25,
                OutputPath = Path.Combine(output, direction + ".dwg") }, flats[0], output);
            Check(result.Placements.Count == flats.Count, "Every sheet in merged real-data set");
        }
    }

    private static void InspectDrawings(string output, string root)
    {
        var records = Directory.EnumerateFiles(root, "*.dwg", SearchOption.AllDirectories).Select(path =>
        {
            var document = ACadSharp.IO.DwgReader.Read(path);
            var entities = document.BlockRecords.SelectMany(block => block.Entities).ToArray();
            return new
            {
                path,
                entities = entities.GroupBy(entity => entity.ObjectName).OrderBy(group => group.Key)
                    .ToDictionary(group => group.Key, group => group.Count()),
                wipeouts = entities.OfType<ACadSharp.Entities.Wipeout>().Select(w => new
                {
                    layer = w.Layer.Name, w.InsertPoint, w.UVector, w.VVector, w.Size,
                    clip = w.ClipBoundaryVertices.ToArray()
                }).ToArray(),
                hatches = entities.OfType<ACadSharp.Entities.Hatch>().Select(h => new
                {
                    layer = h.Layer.Name, h.IsSolid, h.PatternScale, h.PatternAngle,
                    pattern = h.Pattern?.Name, color = new { h.Color.R, h.Color.G, h.Color.B, h.Color.IsByLayer, h.Color.IsByBlock }, paths = h.Paths.Count
                }).ToArray()
            };
        }).ToArray();
        File.WriteAllText(Path.Combine(output, "drawing-inspection.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        _checks += records.Length;
    }

    private static void Managed(string output)
    {
        var sheets = Enumerable.Range(1, 6).Select(i => new SheetDescriptor("sheet-" + i, i, "A10" + i, "구조 평면도 " + i)).ToList();
        var linkedSheet = new SheetDescriptor("sheet-1", 101, "S101", "링크 구조 평면도",
            "link:structure", "구조모델", false, @"C:\Models\Structure.rvt", 1);
        Check(linkedSheet.Key != linkedSheet.UniqueId && linkedSheet.Key.StartsWith("link:structure\u001f")
            && linkedSheet.DisplayNumber == "[구조모델] S101", "Linked sheet has a document-scoped persistent key and source label");
        var crossModelEditor = new SheetSetEditor(new[]
        {
            new SheetSetDefinition { Name = "호스트", TemplateId = "template", SheetUniqueIds = new() { sheets[0].Key } },
            new SheetSetDefinition { Name = "구조 링크", TemplateId = "template", SheetUniqueIds = new() { linkedSheet.Key } }
        });
        crossModelEditor.Select(crossModelEditor.Sets[0].Id, false, false);
        crossModelEditor.Select(crossModelEditor.Sets[1].Id, true, false);
        crossModelEditor.Combine("통합 모델 세트");
        Check(crossModelEditor.Sets.Single().SheetUniqueIds.SequenceEqual(new[] { sheets[0].Key, linkedSheet.Key }),
            "Host and different linked-model sheets combine into one ordered set");
        var singles = sheets.Select(s => new SheetSetDefinition { Name = s.Number, TemplateId = "template", SheetUniqueIds = new() { s.UniqueId } }).ToList();
        var editor = new SheetSetEditor(singles);
        editor.Select(editor.Sets[0].Id, false, false); editor.Select(editor.Sets[2].Id, false, true);
        Check(editor.SelectedIds.Count == 3, "Shift range selection"); editor.Combine("구조 평면 세트");
        Check(editor.Sets.Count == 4 && editor.Sets[0].SheetUniqueIds.SequenceEqual(new[] { "sheet-1", "sheet-2", "sheet-3" }), "Set membership and order");
        editor.MoveSheet(editor.Sets[0].Id, 2, -1);
        Check(editor.Sets[0].SheetUniqueIds.SequenceEqual(new[] { "sheet-1", "sheet-3", "sheet-2" }), "Explicit move buttons");
        Check(singles.All(s => s.SheetUniqueIds.Count == 1), "Draft must not mutate saved sets");
        var grouped = editor.Sets.Select(s => s.Copy()).ToList(); editor.Release(sheets.ToDictionary(s => s.UniqueId));
        Check(editor.Sets.Count == 6 && editor.Sets.SelectMany(s => s.SheetUniqueIds).Distinct().Count() == 6, "Release retains all sheets");
        editor.Select(editor.Sets[0].Id, false, false); editor.Select(editor.Sets[5].Id, true, false); editor.Combine("비연속 선택");
        Check(editor.Sets[0].SheetUniqueIds.Count == 2, "Sheet selection need not inherit Rebar level restrictions");

        string configPath = Path.Combine(output, "settings.json"); var store = new ExportConfigurationStore(configPath);
        var configuration = new RevitExportConfiguration { SheetSets = grouped };
        store.Save(configuration); var loaded = store.Load(); Check(loaded.SheetSets[0].Name == "구조 평면 세트", "Korean config roundtrip");
        loaded.SheetSets[0].Direction = "Vertical"; store.Save(loaded);
        Check(File.Exists(configPath + ".bak") && store.Load().SheetSets[0].Direction == "Vertical", "Atomic save and backup");
        var row = new RevitLayerRow { Category = "구조 기둥", Layer = "S-COL", OriginalLayer = "S-COL", Color = 1, OriginalColor = 1,
            CutLayer = "S-COL-CUT", OriginalCutLayer = "S-COL-CUT", CutColor = 3, OriginalCutColor = 3 };
        Check(!row.HasChanges && RevitLayerMappingService.Validate(new[] { row }).Count == 0, "Unchanged source settings preserved");
        row.Color = 256; Check(RevitLayerMappingService.Validate(new[] { row }).Count > 0, "Reject invalid ACI"); row.Color = 5;
        row.Linetype = "Continuous"; row.Lineweight = 25;
        var conflict = row.Copy(); conflict.Category = "다른 기둥"; conflict.Lineweight = 50;
        Check(RevitLayerMappingService.Validate(new[] { row, conflict }).Count > 0, "Shared layer property conflict");
        string source = Path.Combine(output, "publish-source.tmp"); File.WriteAllText(source, "new");
        string existing = Path.Combine(output, "세트.dwg"); File.WriteAllText(existing, "existing");
        string published = RevitDwgExportService.PublishUnique(source, output, "세트");
        Check(File.ReadAllText(existing) == "existing" && published.EndsWith("_v2.dwg") && File.ReadAllText(published) == "new", "No-overwrite publication");

        Application.SetHighDpiMode(HighDpiMode.SystemAware); Application.EnableVisualStyles();
        SpacingRegression.Run(output, Check, Render);
        OutputSetupRegression.Run(output, Check);
        LayerSearchRegression.Run(output, Check, Render);
        var uiRows = Enumerable.Range(0, 45).Select(i => new RevitLayerRow { Category = i < 15 ? "구조 기둥" : i < 30 ? "벽" : "주석",
            Subcategory = i % 15 == 0 ? "" : "하위 항목 " + i, Layer = "S-COL-" + i, OriginalLayer = "S-COL-" + i,
            CutLayer = "S-CUT-" + i, OriginalCutLayer = "S-CUT-" + i, Color = i + 1, OriginalColor = i + 1, CutColor = 7, OriginalCutColor = 7 }).ToList();
        using (var layers = new LayerRuleManagerForm(store, new(), new[] { "", "프로젝트 출력 설정" }, _ => uiRows.Select(r => r.Copy()).ToList(),
            new[] { new MaterialChoice("material-ui-1", 101, "콘크리트") }))
        {
            Render(layers, Path.Combine(output, "layers.png"));
            layers.Show(); Application.DoEvents();
            DataGridView grid = Descendants(layers).OfType<DataGridView>().Single(g => g.Columns.Contains("Color"));
            DataGridView materialGrid = Descendants(layers).OfType<DataGridView>().Single(g => g.Columns.Contains("Material"));
            Check(!grid.AllowUserToResizeColumns && !grid.AllowUserToResizeRows && !grid.AllowUserToOrderColumns, "Grid resize/reorder locked");
            Check(grid.RowCount == 3 && grid.Columns["Color"].ReadOnly, "Initially collapsed categories and click-only color");
            Check(!Descendants(layers).OfType<TabControl>().Any()
                && !Descendants(layers).OfType<ComboBox>().Any(combo => combo.Items.Cast<object>().Any(item => item.ToString() == "천장평면도")),
                "Material and category rules share one template screen without built-in view-type scopes");
            grid.CurrentCell = grid.Rows[0].Cells[0];
            typeof(LayerRuleManagerForm).GetMethod("AddRule", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(layers, null);
            var custom = (RevitLayerRow)grid.CurrentRow!.DataBoundItem;
            Check(custom.IsCustom && grid.RowCount == 18 && custom.Caption.StartsWith("    └"), "Adding a filter opens its category without expanding others");
            grid.EndEdit(); custom.TypeNameContains = "RC"; custom.Layer = "S-RC"; custom.CutLayer = "S-RC-CUT";
            typeof(LayerRuleManagerForm).GetMethod("AddMaterialRule", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(layers, null);
            Check(materialGrid.RowCount == 1 && ((MaterialLayerRule)materialGrid.Rows[0].DataBoundItem).MaterialUniqueId == "material-ui-1",
                "Material section creates an exact Revit material rule");
            typeof(LayerRuleManagerForm).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(layers, null);
            var savedSetup = store.Load().OutputSetups.First();
            Check(savedSetup.Layers.Any(r => r.IsCustom && r.TypeNameContains == "RC" && r.Layer == "S-RC"), "Custom rules persist in ChangExport settings");
            Check(savedSetup.Layers.Count == 46 && savedSetup.Layers.All(r => r.ViewScope.Length == 0),
                "Saving preserves hidden rows in one scope-free DWG layer template");
            Check(savedSetup.MaterialRules.Single().MaterialUniqueId == "material-ui-1"
                && savedSetup.MaterialRules.Single().ViewScope.Length == 0,
                "Exact material rule persists in the selected DWG layer template");
            Render(layers, Path.Combine(output, "layers-filter.png"));
            layers.Size = layers.MinimumSize; Render(layers, Path.Combine(output, "layers-small.png"));
        }
        using (var colors = new AciColorDialog(3))
        {
            var button = Descendants(colors).OfType<Button>().Single(b => b.AccessibleName == "ACI 5");
            // Invoke the same Click handler without opening an interactive dialog.
            typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, new object[] { EventArgs.Empty });
            Check(colors.SelectedIndex == 5, "Color selection updates ACI value"); Render(colors, Path.Combine(output, "colors.png"));
        }
        var templateChoices = new[] { new LayerTemplateChoice("template", "구조도 DWG") };
        var assignments = sheets.ToDictionary(s => s.UniqueId, _ => "template");
        using (var sets = new SheetGroupManagerForm(sheets, grouped, templateChoices, assignments))
        { Render(sets, Path.Combine(output, "sets.png")); Check(Descendants(sets).OfType<ComboBox>().Any(c => c.Text == "구조도 DWG"), "Sheet cards expose their assigned DWG layer template"); sets.Size = sets.MinimumSize; Render(sets, Path.Combine(output, "sets-small.png")); }
        using (var export = new ExportSettingsForm(sheets, grouped, templateChoices, assignments, 2500, output, (_, _) => { }))
        {
            Render(export, Path.Combine(output, "export.png")); Check(export.SelectedSets.Count == grouped.Count, "Export selects whole sets");
            Check(!Descendants(export).OfType<TextBox>().Any(t => t.AccessibleName == "전역폭 판별 문자열")
                && !Descendants(export).OfType<Button>().Any(b => b.Text == "배치 간격 설정"), "Export window no longer duplicates preferences controls");
            export.Size = export.MinimumSize; Render(export, Path.Combine(output, "export-small.png"));
        }
        var exportResult = new ExportRunResult { OutputFolder = output, WorkFolder = output, ManifestPath = "Manifest.json" };
        var warningItem = new ExportItemResult("구조 세트", "", true, "구조 세트.dwg");
        warningItem.Warnings.Add("시트 A101: 생략: 원근·음영 뷰포트. 다른 도면은 저장했습니다.");
        warningItem.Warnings.Add("시트 A102: 대체: 이미지 '로고.png'를 사각형으로 표시했습니다."); exportResult.Items.Add(warningItem);
        using (var resultForm = new ExportResultForm(exportResult)) Render(resultForm, Path.Combine(output, "result-warnings.png"));
        Check(!File.Exists(Path.Combine(output, "unexpected.json")), "No UI execution side effects");
    }
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Render(Form form, string path)
    {
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
        form.Show(); Application.DoEvents(); form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path);
        form.Hide();
    }
    private static void IndependentDwg(string output, string fixture)
    {
        var processor = new ManagedDwgProcessor(); string inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
        string flatPath = Path.Combine(output, "flat.dwg");
        Console.WriteLine("Flatten synthetic sheet...");
        var flat = processor.Run(new BridgeRequest { Operation = "Flatten", OutputPath = flatPath }, fixture, output);
        Check(flat.ModelEntityCount > 0 && flat.PaperEntityCount == 0, "Flattened output contains model-space entities only");
        Check(File.ReadAllBytes(flatPath).Take(6).SequenceEqual(File.ReadAllBytes(fixture).Take(6)), "Flatten preserves DWG version");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))) == inputHash, "Input DWG preserved");
        foreach (string direction in new[] { "Horizontal", "Vertical" })
        {
            Console.WriteLine("Merge 3 sheets: " + direction);
            var response = processor.Run(new BridgeRequest { Operation = "Merge", OutputPath = Path.Combine(output, direction + ".dwg"),
                Inputs = new() { flatPath, flatPath, flatPath }, Direction = direction, MarginMm = 50 }, flatPath, output);
            Check(response.ModelEntityCount == 3 && response.PaperEntityCount == 0 && response.EntityBounds.Count == 3, "Three actual model-space blocks");
            Check(File.ReadAllBytes(response.OutputPath).Take(6).SequenceEqual(File.ReadAllBytes(fixture).Take(6)), "Merge preserves DWG version");
            for (int i = 0; i < 3; i++)
            {
                var actual = response.EntityBounds[i]; var planned = response.Placements[i];
                Near(actual.X, planned.X, "Reopened block X"); Near(actual.Y, planned.Y, "Reopened block Y");
                Near(actual.Width, planned.Width, "Reopened block width"); Near(actual.Height, planned.Height, "Reopened block height");
                if (i == 0) continue;
                var previous = response.EntityBounds[i - 1];
                if (direction == "Horizontal") Near(actual.X - previous.X - previous.Width, 50, "Horizontal actual gap");
                else Near(previous.Y - actual.Y - actual.Height, 50, "Vertical actual gap");
            }
            File.WriteAllText(Path.Combine(output, direction + "-inspection.json"), JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        }
        string conflict = Path.Combine(output, "Horizontal.dwg"); string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(conflict)));
        bool refused = false;
        try { processor.Run(new BridgeRequest { Operation = "Merge", OutputPath = conflict }, flatPath, output); } catch (IOException) { refused = true; }
        Check(refused && hash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(conflict))), "Existing DWG rejected without modifying the file");
        Console.WriteLine("Check missing linetype failure...");
        string invalidOutput = Path.Combine(output, "invalid-style.dwg"); bool invalidRejected = false;
        try
        {
            processor.Run(new BridgeRequest { Operation = "Merge", OutputPath = invalidOutput, Inputs = new() { flatPath },
                LayerStyles = new() { new LayerAppearance { Layer = "0", Linetype = "CHANG_NONEXISTENT_LINETYPE" } } }, flatPath, output);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Linetype missing")) { invalidRejected = true; }
        Check(invalidRejected && !File.Exists(invalidOutput), "Missing linetype fails before final output");
        Console.WriteLine("Check cancellation...");
        bool cancelled = false;
        try { processor.Run(new BridgeRequest { Operation = "Flatten", OutputPath = Path.Combine(output, "cancelled.dwg") }, fixture, output, () => true); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !File.Exists(Path.Combine(output, "cancelled.dwg")), "Cancellation publishes no DWG");
        Assembly engine = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "ACadSharp");
        Check(engine.Location.Length == 0, "DWG engine loaded from ChangExport.dll embedded resource, not an installed CAD application");
        Check(!typeof(ManagedDwgProcessor).Assembly.GetManifestResourceNames().Any(n => n.Contains("AutoCadBridge")), "No external CAD bridge shipped");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "acdbmgd" or "accoremgd" or "AcExportLayout"), "No AutoCAD runtime assembly loaded");
        DwgRegression.Run(output, Check, Near);
    }
}
