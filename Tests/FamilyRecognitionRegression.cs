using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using CSMath;
using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;
using ChangExport.Standards;
using ChangExport.UI;

internal static class FamilyRecognitionRegression
{
    internal static void Run(string output, Action<bool, string> check, string? manifestPath)
    {
        Policy(check);
        Selection(output, check);
        Synthetic(output, check);
        if (manifestPath != null) Real(output, manifestPath, check);
    }

    private static void Policy(Action<bool, string> check)
    {
        var type = typeof(ManagedDwgProcessor).Assembly.GetType("ChangExport.Export.ExportGeometryOptions")!;
        var method = type.GetMethod("BlockExclusion", BindingFlags.Static | BindingFlags.NonPublic)!;
        var category = Assembly.Load("RevitAPI").GetType("Autodesk.Revit.DB.BuiltInCategory")!;
        string Reason(string name, bool model, bool view, bool inPlace, bool selected)
        {
            object[] args = { Convert.ToInt64(Enum.Parse(category, name)), model, view, inPlace, selected, false };
            return (string)method.Invoke(null, args)!;
        }
        foreach (var name in new[] { "OST_Doors", "OST_Windows", "OST_Columns", "OST_StructuralColumns" })
            check(Reason(name, true, false, false, false) == "", "Model family allowed: " + name);
        check(Reason("OST_TitleBlocks", false, true, false, false) == "", "Sheet titleblock is the annotation exception");
        foreach (var name in new[] { "OST_StructuralFraming", "OST_StructuralFoundation", "OST_Walls", "OST_Floors" })
            check(Reason(name, true, false, false, true) != "", "Protected category cannot be enabled by selection: " + name);
        check(Reason("OST_GenericModel", true, false, false, false) != "", "Generic models are not automatically grouped");
        check(Reason("OST_GenericModel", true, false, false, true) == "", "Explicitly selected elevator/tactile model family allowed");
        check(Reason("OST_GenericModel", true, false, true, true) != "", "In-place family stays excluded");
        check(Reason("OST_GenericAnnotation", false, true, false, true) != "", "2D annotation family stays excluded");
        check(Reason("OST_DetailComponents", false, true, false, true) != "", "Detail components stay excluded");
    }

    private static void Selection(string output, Action<bool, string> check)
    {
        var choices = new[] { new BlockFamilyChoice("elevator", "EV_17인승", "일반 모델"), new BlockFamilyChoice("braille", "점자블럭(원형)", "일반 모델"),
            new BlockFamilyChoice("furniture", "테이블", "가구") };
        string path = Path.Combine(output, "family-settings.json"); var store = new ExportConfigurationStore(path);
        var config = new RevitExportConfiguration { AdditionalBlockFamilyIds = new() { "unloaded-link", "elevator" } }; store.Save(config);
        string before = File.ReadAllText(path);
        using var form = new FamilyBlockSelectionForm(choices, config.AdditionalBlockFamilyIds);
        Render(form, Path.Combine(output, "family-settings.png"));
        var list = Controls(form).OfType<CheckedListBox>().Single(); var search = Controls(form).OfType<TextBox>().Single();
        search.Text = "점자"; check(list.Items.Count == 1, "Search returns only matching families"); list.SetItemChecked(0, true);
        search.Text = "없는 검색"; check(list.Items.Count == 0, "No false-positive search results");
        search.Text = "";
        check(form.SelectedIds.Order().SequenceEqual(new[] { "braille", "elevator", "unloaded-link" }.Order()), "Selection survives filtering and unloaded links");
        check(config.AdditionalBlockFamilyIds.SequenceEqual(new[] { "unloaded-link", "elevator" }) && File.ReadAllText(path) == before, "Draft selection does not mutate configuration or file");
        form.Size = form.MinimumSize; Render(form, Path.Combine(output, "family-settings-small.png"));
        CheckBounds(form, check);
        Controls(form).OfType<Button>().Single(b => b.Text == "취소").PerformClick();
        check(form.DialogResult == DialogResult.Cancel && File.ReadAllText(path) == before, "Cancel does not save");
        using var apply = new FamilyBlockSelectionForm(choices, new[] { "braille", "elevator", "unloaded-link" });
        Render(apply, Path.Combine(output, "family-settings-selected.png"));
        Controls(apply).OfType<Button>().Single(b => b.Text == "선택 적용").PerformClick();
        check(apply.DialogResult == DialogResult.OK, "Selection can be applied");
        config.AdditionalBlockFamilyIds = apply.SelectedIds.ToList(); store.Save(config);
        check(store.Load().AdditionalBlockFamilyIds.SequenceEqual(apply.SelectedIds) && store.Load().WideLineKeyword == "##", "Selection persists without changing wide-line setting");
        using var scaled = new FamilyBlockSelectionForm(choices, apply.SelectedIds);
        scaled.Scale(new SizeF(1.5f, 1.5f)); Render(scaled, Path.Combine(output, "family-settings-150.png")); CheckBounds(scaled, check);
        int opens = 0;
        using var settings = new ChangExportSettingsForm("204동 건축 도면", "##", new[] { new SheetSetDefinition { Name = "AA-440", SheetUniqueIds = new() { "a", "b" } } }, (_, _) => { }, _ => opens++);
        Render(settings, Path.Combine(output, "settings-preview.png"));
        Controls(settings).OfType<Button>().Single(b => b.Text == "패밀리 블록…").PerformClick();
        check(opens == 1, "Separate family settings button is connected");
        settings.Size = settings.MinimumSize; Render(settings, Path.Combine(output, "settings-small.png")); CheckBounds(settings, check);
    }

    private static IEnumerable<Control> Controls(Control owner) => owner.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Controls(c)));
    private static void Render(Form form, string path)
    {
        form.StartPosition = FormStartPosition.Manual; form.Location = new System.Drawing.Point(-30000, -30000); form.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(path);
    }
    private static void CheckBounds(Form form, Action<bool, string> check)
    {
        foreach (var c in Controls(form).Where(c => c is Label or Button or TextBox or CheckedListBox))
        for (Control? parent = c.Parent; parent != null; parent = parent.Parent)
        {
            var origin = parent.PointToClient(c.Parent!.PointToScreen(c.Location));
            check(parent.ClientRectangle.Contains(new Rectangle(origin, c.Size)), "Setting control not clipped: " + c.Text);
        }
    }

    private static void Synthetic(string output, Action<bool, string> check)
    {
        var doc = (CadDocument)typeof(EditableModelRegression).GetMethod("Sheet", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { 100 })!;
        var names = new[] { "문 - D1-7001-평면", "문 - D1-V1-평면", "문 - D1-7002-평면", "문 - D2-7003-평면",
            "문 - D1추가-101-평면", "동명이형 - A-900-평면", "철골 보 - B1-V1-평면", "점자블럭(원형) - 2EA-V1-평면",
            "알수없음 - Z-12-평면", "문 - D1-평면", "철골 보 - B1-301-평면" };
        for (int index = 0; index < names.Length; index++)
        {
            var block = new BlockRecord(names[index]);
            block.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(30, 0, 0) });
            block.Entities.Add(new Arc { Center = XYZ.Zero, Radius = 30, StartAngle = Math.PI, EndAngle = Math.PI * 1.5 });
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(150 + index * 150, 800, 0), Rotation = index == 0 ? Math.PI / 2 : 0 });
        }
        var sources = new List<FamilyBlockSource> {
            new() { Identity = "D1", Label = "문 - D1", NativeLabels = new() { "문 - D1" }, NativePrefixes = new() { "문 - D1-101-" } },
            new() { Identity = "D2", Label = "문 - D2", NativeLabels = new() { "문 - D2" } },
            new() { Identity = "host:A", Label = "동명이형 - A", NativeLabels = new() { "동명이형 - A" } },
            new() { Identity = "link:A", Label = "동명이형 - A", NativeLabels = new() { "동명이형 - A" } },
            new() { Identity = "B1", Label = "철골 보 - B1", ExclusionReason = "보·벽·바닥·기초",
                NativeLabels = new() { "철골 보 - B1" }, NativePrefixes = new() { "철골 보 - B1-301-" } },
            new() { Identity = "braille", Label = "점자블럭_원형_ - 2EA", NativeLabels = new() { "점자블럭_원형_ - 2EA" } }
        };
        string native = Path.Combine(output, "recognition-native.dwg"); DwgWriter.Write(native, doc);
        var processor = new ManagedDwgProcessor();
        string grouped = Path.Combine(output, "recognition-grouped.dwg");
        var result = processor.Run(new BridgeRequest { Operation = "Flatten", RevitSheet = true, UseLayerColors = true,
            FamilySources = sources, OutputPath = grouped }, native, output);
        check(result.FamilyBlockReferences == 5, "Known whole names match changed IDs and V1, excluding unknown/truncated/ambiguous/beam names");
        check(result.FamilyBlockDefinitions < result.FamilyBlockReferences, "Identical same-type geometry reuses a definition");
        check(result.FamilyBlockMatches.Any(m => m.Status.Contains("중복")), "Ambiguous names are diagnosed");
        check(result.FamilyBlockMatches.Count(m => m.Status.StartsWith("제외:")) == 2, "Beam excluded even on exact ID or variant name");
        check(result.FamilyBlockMatches.Any(m => m.NativeBlock.StartsWith("알수없음") && m.Status.Contains("미연결")), "Unknown families are diagnosed");
        string plain = Path.Combine(output, "recognition-plain.dwg"), exploded = Path.Combine(output, "recognition-exploded.dwg");
        processor.Run(new BridgeRequest { Operation = "Flatten", RevitSheet = true, UseLayerColors = true, OutputPath = plain }, native, output);
        processor.Run(new BridgeRequest { Operation = "Merge", RevitSheet = true, UseLayerColors = true, Inputs = new() { grouped }, OutputPath = exploded }, grouped, output);
        PerformanceRegression.Compare(plain, exploded, check, normalizePeriodicAngles: true);
    }

    private static void Real(string output, string manifestPath, Action<bool, string> check)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = json.RootElement;
        string root = Path.Combine(manifest.GetProperty("WorkFolder").GetString()!, "set_001");
        var paths = Directory.GetFiles(root, "*.dwg", SearchOption.AllDirectories);
        var hashes = paths.ToDictionary(p => p, Hash);
        var layers = JsonSerializer.Deserialize<List<LayerAppearance>>(manifest.GetProperty("layerColors").GetRawText())!;
        var wide = JsonSerializer.Deserialize<List<WideLineLayer>>(manifest.GetProperty("wideLineLayers").GetRawText())!;
        var sources = new Dictionary<string, FamilyBlockSource>();
        // Engine-only fixture: explicit families reported by the user. These names
        // stand in for Revit's inventory, not a production DWG-name category filter.
        string[] prefixes = { "EV_17인승 - ", "외여닫이문 - ", "세대현관문 - ", "자동문편개형_로비폰_ - ", "점검구_외여닫이_1 - ",
            "점자블럭_원형_개수_면기반_ - ", "점자블럭_원형_ - ", "고정창3ea - ", "그릴창_고정_ - ", "그릴창_고정__3짝 - ",
            "철골 기둥_H형강_ - ", "LAON_LH_Title Block_A1 - ", "철골 보_H형강_ - " };
        foreach (string path in paths)
        foreach (var block in DwgReader.Read(path).BlockRecords.Where(b => prefixes.Any(p => b.Name.StartsWith(p, StringComparison.Ordinal))))
        {
            var suffix = Regex.Match(block.Name, @"-(?:[0-9]+|V[0-9]+)-");
            if (!suffix.Success) continue;
            string label = block.Name[..suffix.Index];
            sources.TryAdd(label, new() { Identity = "fixture:" + label, Label = label, NativeLabels = new() { label },
                IsTitleBlock = label.StartsWith("LAON_LH_Title"), ExclusionReason = label.StartsWith("철골 보") ? "보·벽·바닥·기초" : "" });
        }
        var processor = new ManagedDwgProcessor();
        var sheets = paths.Where(p => Path.GetFileName(p) == "sheet.dwg").OrderBy(p => p).ToArray();
        PreparedDrawing[] Prepare(bool family) => sheets.Select(p => processor.Prepare(new BridgeRequest { RevitSheet = true, UseLayerColors = true,
            LayerStyles = layers, WideLineLayers = wide, FamilySources = family ? sources.Values.ToList() : new() }, p)).ToArray();
        var prepared = Prepare(true);
        var matches = prepared.SelectMany(p => p.Response.FamilyBlockMatches).ToArray();
        string grouped = Path.Combine(output, "AA-440_family-blocks.dwg"), baseline = Path.Combine(output, "AA-440_plain.dwg"), exploded = Path.Combine(output, "AA-440_reexpanded.dwg");
        var result = processor.MergePrepared(new BridgeRequest { RevitSheet = true, UseLayerColors = true, LayerStyles = layers, OutputPath = grouped }, prepared, output);
        processor.MergePrepared(new BridgeRequest { RevitSheet = true, UseLayerColors = true, LayerStyles = layers, OutputPath = baseline }, Prepare(false), output);
        processor.Run(new BridgeRequest { Operation = "Merge", RevitSheet = true, UseLayerColors = true, Inputs = new() { grouped }, OutputPath = exploded }, grouped, output);
        PerformanceRegression.Compare(baseline, exploded, check, normalizePeriodicAngles: true);
        var actual = DwgReader.Read(grouped);
        var inserts = actual.Entities.OfType<Insert>().ToArray();
        check(inserts.Any(i => i.Block.Name.Contains("EV_17인승")), "Actual elevators become blocks");
        check(inserts.Count(i => i.Block.Name.Contains("외여닫이문") || i.Block.Name.Contains("세대현관문") || i.Block.Name.Contains("점검구_") || i.Block.Name.Contains("자동문")) >= 22, "Actual doors including swing doors become blocks");
        check(inserts.Any(i => i.Block.Name.Contains("점자블럭")), "Actual tactile families become blocks");
        check(inserts.Any(i => i.Block.Name.Contains("고정창")), "Actual windows become blocks");
        check(inserts.Any(i => i.Block.Name.Contains("철골 기둥")), "Columns allowed separately from beams");
        check(inserts.All(i => !i.Block.Name.Contains("철골 보")), "Actual steel beams stay primitives");
        check(inserts.Count(i => i.Block.Name.Contains("LAON_LH_Title")) == 2, "Two titleblock references retained");
        check(actual.Entities.OfType<LwPolyline>().Count(p => Math.Abs(p.ConstantWidth - 240) < 1e-6) == 101, "All 101 wide polylines retained");
        check(actual.Layers.All(l => l.Color.Index == 7) && actual.BlockRecords.SelectMany(b => b.Entities).All(e => e.Color.IsByLayer), "All colors retained");
        check(result.Success && result.PaperEntityCount == 0 && result.Placements.Count == 2, "Both sheets remain in model space");
        check(Math.Abs(result.Placements[1].X - result.Placements[0].Width) < 1e-5, "Zero gap and multi-scale preserved");
        check(hashes.All(p => Hash(p.Key) == p.Value), "Original DWGs untouched");
        File.WriteAllText(Path.Combine(output, "family-review.json"), JsonSerializer.Serialize(new { result, matches,
            counts = inserts.GroupBy(i => Regex.Replace(i.Block.Name, @"_CE_FAMILY_.*", "")).Select(g => new { family = g.Key, references = g.Count() }),
            bytesGrouped = new FileInfo(grouped).Length, bytesPlain = new FileInfo(baseline).Length }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
