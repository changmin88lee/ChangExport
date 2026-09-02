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
        check(walls.Length == 186 && walls.Where(e => e is not (Hatch or Solid or Wipeout)).All(e => e.Color.IsByLayer && e.Layer.Color.Index == 7),
            "Reported wall linework follows the white layer while fill appearance remains independent");
        check(document.Entities.Where(e => e is not (Hatch or Solid or Wipeout)).All(e => e.Color.IsByLayer && e.Layer.Color.Index == 7),
            "Every non-fill final entity follows the saved default layer color");
        check(prepared.Sum(p => p.Response.NormalizedEntityColors + p.Response.PreservedFillColors) >= 1343,
            "Previously overridden actual entities are accounted for as normalized linework or preserved fills");
        PerformanceRegression.Compare(snapshot, target, check, ignoreColors: true, normalizePeriodicAngles: true);
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
        var invisibleLayer = new Layer(RevitLayerMappingService.InvisibleLineExportLayer); source.Layers.Add(invisibleLayer);
        var revitRenamedLayer = new Layer("선__04_하늘_") { Color = new Color(0, 166, 0) }; source.Layers.Add(revitRenamedLayer);
        source.Entities.Add(new Line { Layer = invisibleLayer, StartPoint = new XYZ(1, 1, 0), EndPoint = new XYZ(9, 9, 0) });
        source.Entities.Add(new Line { Layer = revitRenamedLayer, StartPoint = new XYZ(2, 2, 0), EndPoint = new XYZ(8, 8, 0) });
        var dim = source.Entities.OfType<Dimension>().Single();
        var changed = (DimensionStyle)dim.Style.Clone(); changed.TextColor = new Color(50); changed.DimensionLineColor = new Color(10);
        changed.ExtensionLineColor = new Color(8); changed.ArrowSize = 9; dim.SetDimensionOverride(changed);
        source.Entities.Add(new Line { Layer = source.Layers["S-COL"], StartPoint = XYZ.Zero, EndPoint = new XYZ(10, 10, 0), Color = new Color(200) });
        source.Entities.Add(new Line { Layer = source.Layers["S-COL"], StartPoint = XYZ.Zero, EndPoint = new XYZ(20, 20, 0), Color = new Color(201) });
        source.PaperSpace.Entities.Add(new MText { Value = @"{\C10;문자}\P\H2x;{\c16711680;색상}\P\\C10;literal", Height = 2, Color = new Color(255, 0, 0) });
        string input = Path.Combine(output, "colors-source.dwg"), target = Path.Combine(output, "colors-filtered.dwg");
        DwgWriter.Write(input, source);
        var request = new BridgeRequest { Operation = "Flatten", UseLayerColors = true, OutputPath = target,
            LayerStyles = source.Layers.Where(layer => layer != revitRenamedLayer)
                .Select(layer => new LayerAppearance { Layer = layer.Name, Color = 7 })
                .Append(new LayerAppearance { Layer = "선_#04(하늘)", Color = 7 }).ToList(),
            ExcludedLayers = new() { RevitLayerMappingService.InvisibleLineExportLayer },
            ColorRemaps = new() { new() { MarkerAci = 200, Layer = "CUSTOM-P", Color = 3, RuleId = "type" }, new() { MarkerAci = 201, Layer = "CUSTOM-C", Color = 5, RuleId = "type" } } };
        var response = new ManagedDwgProcessor().Run(request, input, output);
        var saved = DwgReader.Read(target); var all = saved.BlockRecords.SelectMany(b => b.Entities).ToArray();
        var fills = all.Where(e => e is Hatch or Solid).ToArray();
        check(fills.Length > 0 && fills.All(e => !e.Color.IsByLayer && e.Color.R == new Color(10).R && e.Color.G == new Color(10).G && e.Color.B == new Color(10).B),
            "Hatch and solid fill colors remain explicit view colors independent of layer ACI");
        check(all.Where(e => e is not (Hatch or Solid or Wipeout)).All(e => e.Color.IsByLayer),
            "Non-fill line, text, dimension and insert colors continue to follow layers");
        check(response.ExcludedEntities == 1
            && !all.Any(e => e.Layer.Name == RevitLayerMappingService.InvisibleLineExportLayer),
            $"Revit invisible-line geometry is removed: excluded={response.ExcludedEntities}, remaining={all.Count(e => e.Layer.Name == RevitLayerMappingService.InvisibleLineExportLayer)}");
        check(saved.Layers["CUSTOM-P"].Color.Index == 3 && saved.Layers["CUSTOM-C"].Color.Index == 5
            && all.OfType<Line>().Any(e => e.Layer.Name == "CUSTOM-P") && all.OfType<Line>().Any(e => e.Layer.Name == "CUSTOM-C"),
            "Projection and cut filter markers are resolved before normalization; custom colors are retained");
        check(saved.Layers["선__04_하늘_"].Color.Index == 7
            && all.OfType<Line>().Any(line => line.Layer.Name == "선__04_하늘_" && line.Color.IsByLayer),
            "Revit-rewritten # and parenthesis layer names receive the configured style through canonical matching");
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
