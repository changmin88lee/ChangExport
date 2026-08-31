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
        string output = Path.GetFullPath(args.Length > 1 ? args[1] : "bin/Verification/Tests");
        Directory.CreateDirectory(output);
        try
        {
            if (args.Length > 0 && args[0] == "dwg") IndependentDwg(output, args[2]);
            else Managed(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = true, checks = _checks, mode = args.FirstOrDefault() ?? "managed", time = DateTimeOffset.Now }));
            Console.WriteLine($"PASS: {_checks} checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < 0.0001, $"{message}: {actual} != {expected}");

    private static void Managed(string output)
    {
        var sheets = Enumerable.Range(1, 6).Select(i => new SheetDescriptor("sheet-" + i, i, "A10" + i, "구조 평면도 " + i)).ToList();
        var singles = sheets.Select(s => new SheetSetDefinition { Name = s.Number, SheetUniqueIds = new() { s.UniqueId } }).ToList();
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
        var uiRows = Enumerable.Range(0, 45).Select(i => new RevitLayerRow { Category = i < 15 ? "구조 기둥" : i < 30 ? "벽" : "주석",
            Subcategory = i % 15 == 0 ? "" : "하위 항목 " + i, Layer = "S-COL-" + i, OriginalLayer = "S-COL-" + i,
            CutLayer = "S-CUT-" + i, OriginalCutLayer = "S-CUT-" + i, Color = i + 1, OriginalColor = i + 1, CutColor = 7, OriginalCutColor = 7 }).ToList();
        using (var layers = new LayerRuleManagerForm(store, new(), new[] { "", "프로젝트 출력 설정" }, _ => uiRows.Select(r => r.Copy()).ToList()))
        {
            Render(layers, Path.Combine(output, "layers.png"));
            DataGridView grid = Descendants(layers).OfType<DataGridView>().Single();
            Check(!grid.AllowUserToResizeColumns && !grid.AllowUserToResizeRows && !grid.AllowUserToOrderColumns, "Grid resize/reorder locked");
            Check(grid.RowCount == 45 && grid.Columns["Color"].ReadOnly, "Full mapping and click-only color");
            layers.Size = layers.MinimumSize; Render(layers, Path.Combine(output, "layers-small.png"));
        }
        using (var colors = new AciColorDialog(3))
        {
            var button = Descendants(colors).OfType<Button>().Single(b => b.AccessibleName == "ACI 5");
            // Invoke the same Click handler without opening an interactive dialog.
            typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, new object[] { EventArgs.Empty });
            Check(colors.SelectedIndex == 5, "Color selection updates ACI value"); Render(colors, Path.Combine(output, "colors.png"));
        }
        using (var sets = new SheetGroupManagerForm(sheets, grouped))
        { Render(sets, Path.Combine(output, "sets.png")); sets.Size = sets.MinimumSize; Render(sets, Path.Combine(output, "sets-small.png")); }
        using (var export = new ExportSettingsForm(sheets, grouped, new[] { "", "프로젝트 출력 설정" }, "", output, _ => { }))
        { Render(export, Path.Combine(output, "export.png")); Check(export.SelectedSets.Count == grouped.Count, "Export selects whole sets"); }
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
