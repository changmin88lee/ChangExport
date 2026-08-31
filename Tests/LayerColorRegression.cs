using System.Security.Cryptography;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using ChangExport.Standards;
using CSMath;
using Color = ACadSharp.Color;

internal static class LayerColorRegression
{
    public static void Run(string output, string original, string profile, Action<bool, string> check)
    {
        Synthetic(output, check);
        byte[] originalBytes = ReadShared(original);
        string hash = Convert.ToHexString(SHA256.HashData(originalBytes));
        string snapshot = Path.Combine(output, "original-readonly-snapshot.dwg"); File.WriteAllBytes(snapshot, originalBytes);
        using var manifest = Directory.EnumerateFiles(Path.GetDirectoryName(original)!, "ChangExport_Manifest_*.json")
            .Select(p => JsonDocument.Parse(File.ReadAllText(p))).First(m => m.RootElement.GetProperty("items").EnumerateArray()
                .Any(i => i.GetProperty("Message").GetString() == original));
        var set = manifest.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("Message").GetString() == original);
        var paths = set.GetProperty("Placements").EnumerateArray().Select(p => p.GetProperty("Source").GetString()!).ToArray();
        var sourceHashes = paths.SelectMany(p => Directory.GetFiles(Path.GetDirectoryName(p)!, "*.dwg")).Distinct()
            .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var config = new ExportConfigurationStore(profile).Load();
        var rows = config.OutputSetups.Single(s => s.SetupName == "").Layers;
        check(rows.Count == 1018 && rows.All(r => r.Color == 7 && r.CutColor == 7), "Actual saved default maps all 1018 rows to ACI 7");
        var styles = RevitLayerMappingService.GetAppearances(rows);
        var processor = new ManagedDwgProcessor();
        var prepared = paths.Select(p => processor.Prepare(new BridgeRequest { RevitSheet = true, UseLayerColors = true, LayerStyles = styles }, p)).ToArray();
        string target = Path.Combine(output, "AA-440-layer-colors.dwg");
        var response = processor.MergePrepared(new BridgeRequest { RevitSheet = true, UseLayerColors = true, LayerStyles = styles, OutputPath = target }, prepared, output);
        var document = DwgReader.Read(target);
        check(document.Entities.Count == 13555 && response.PaperEntityCount == 0, "Actual two-sheet model-space entity count retained");
        var walls = document.Entities.Where(e => e.Layer.Name == "벽_하지재_절단").ToArray();
        check(walls.Length == 186 && walls.All(e => e.Color.IsByLayer && e.Layer.Color.Index == 7), "All 186 reported red wall lines now follow the white layer");
        check(document.Entities.All(e => e.Color.IsByLayer && e.Layer.Color.Index == 7), "Every final entity follows the saved default, including other forced colors");
        check(prepared.Sum(p => p.Response.NormalizedEntityColors) >= 1343, "Previously overridden actual entities are accounted for");
        PerformanceRegression.Compare(snapshot, target, check, ignoreColors: true);
        check(Convert.ToHexString(SHA256.HashData(ReadShared(original))) == hash
            && sourceHashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "All original final and native DWGs unchanged");
        File.WriteAllText(Path.Combine(output, "color-result.json"), JsonSerializer.Serialize(new { target, response,
            correctedEntityColors = prepared.Sum(p => p.Response.NormalizedEntityColors) }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static byte[] ReadShared(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream(); source.CopyTo(copy); return copy.ToArray();
    }

    private static void Synthetic(string output, Action<bool, string> check)
    {
        var source = DwgRegression.Sheet(ACadVersion.AC1024);
        foreach (var entity in source.BlockRecords.SelectMany(b => b.Entities)) entity.Color = new Color(10);
        var dim = source.Entities.OfType<Dimension>().Single();
        var changed = (DimensionStyle)dim.Style.Clone(); changed.TextColor = new Color(50); changed.DimensionLineColor = new Color(10);
        changed.ExtensionLineColor = new Color(8); changed.ArrowSize = 9; dim.SetDimensionOverride(changed);
        source.Entities.Add(new Line { Layer = source.Layers["S-COL"], StartPoint = XYZ.Zero, EndPoint = new XYZ(10, 10, 0), Color = new Color(200) });
        source.Entities.Add(new Line { Layer = source.Layers["S-COL"], StartPoint = XYZ.Zero, EndPoint = new XYZ(20, 20, 0), Color = new Color(201) });
        source.PaperSpace.Entities.Add(new MText { Value = @"{\C10;문자}\P\H2x;{\c16711680;색상}\P\\C10;literal", Height = 2, Color = new Color(255, 0, 0) });
        string input = Path.Combine(output, "colors-source.dwg"), target = Path.Combine(output, "colors-filtered.dwg");
        DwgWriter.Write(input, source);
        var request = new BridgeRequest { Operation = "Flatten", UseLayerColors = true, OutputPath = target,
            LayerStyles = source.Layers.Select(l => new LayerAppearance { Layer = l.Name, Color = 7 }).ToList(),
            ColorRemaps = new() { new() { MarkerAci = 200, Layer = "CUSTOM-P", Color = 3, RuleId = "type" }, new() { MarkerAci = 201, Layer = "CUSTOM-C", Color = 5, RuleId = "type" } } };
        new ManagedDwgProcessor().Run(request, input, output);
        var saved = DwgReader.Read(target); var all = saved.BlockRecords.SelectMany(b => b.Entities).ToArray();
        check(all.All(e => e.Color.IsByLayer), "Nested line, hatch, text, dimension and insert colors follow layers");
        check(saved.Layers["CUSTOM-P"].Color.Index == 3 && saved.Layers["CUSTOM-C"].Color.Index == 5
            && all.OfType<Line>().Any(e => e.Layer.Name == "CUSTOM-P") && all.OfType<Line>().Any(e => e.Layer.Name == "CUSTOM-C"),
            "Projection and cut filter markers are resolved before normalization; custom colors are retained");
        check(all.OfType<MText>().Any(m => m.Value == @"{문자}\P\H2x;{색상}\P\\C10;literal"), "Inline indexed/true colors removed without changing font/height or escaped literal text");
        check(saved.DimensionStyles.All(s => s.TextColor.IsByLayer && s.DimensionLineColor.IsByLayer && s.ExtensionLineColor.IsByLayer), "Dimension style color overrides removed");
        // Isolate the new color pass from the SDK's existing Clone behavior (which
        // drops dimension XData). Verify that this pass itself preserves non-color data.
        ManagedDwgProcessor.NormalizeLayerColors(source, new BridgeResponse(), () => { });
        var overrides = dim.GetStyleOverrideMap();
        check(!overrides.DxfProperties.ContainsKey(176) && !overrides.DxfProperties.ContainsKey(177) && !overrides.DxfProperties.ContainsKey(178)
            && overrides.DxfProperties.ContainsKey(41), "Dimension color overrides removed while arrow-size override is retained");
        check(ManagedDwgProcessor.RemoveInlineColors(@"\\C10;\Cbad;\C10;ok") == @"\\C10;\Cbad;ok", "Only complete unescaped color commands are stripped");
    }
}
