using System.Security.Cryptography;
using System.Text.Json;
using ACadSharp.Entities;
using ACadSharp.IO;
using ChangExport.DwgProcessing;

internal static class FillAppearanceRegression
{
    public static void Run(string output, string nativeDirectory, Action<bool, string> check)
    {
        string input = Path.Combine(nativeDirectory, "sheet.dwg");
        check(File.Exists(input), "Actual Revit native sheet exists");
        var sources = Directory.GetFiles(nativeDirectory, "*.dwg");
        var hashes = sources.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var styles = sources.SelectMany(path => DwgReader.Read(path).Layers)
            .GroupBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LayerAppearance { Layer = group.Key, Color = 7 }).ToList();
        string target = Path.Combine(output, "actual-fill-appearance.dwg");
        var response = new ManagedDwgProcessor().Run(new BridgeRequest
        {
            Operation = "Flatten", RevitSheet = true, UseLayerColors = true,
            OutputPath = target, LayerStyles = styles
        }, input, output);
        var saved = DwgReader.Read(target);
        var entities = DwgRegression.Walk(saved.ModelSpace).ToArray();
        var hatches = entities.OfType<Hatch>().ToArray();
        check(response.Success && response.PaperEntityCount == 0 && response.PreservedFillColors >= hatches.Length,
            "Actual Revit fills are reported and saved in model space");
        check(hatches.Length > 0 && hatches.All(h => !h.Color.IsByLayer && !h.Color.IsByBlock),
            "Every actual Revit hatch has an explicit display RGB independent of its layer");
        check(hatches.Any(h => !h.IsSolid && !string.IsNullOrWhiteSpace(h.Pattern?.Name) && h.PatternScale > 0),
            "Actual patterned fill retains its pattern name and positive scale");
        check(entities.Where(entity => entity is not (Hatch or Solid or Wipeout)).All(entity => entity.Color.IsByLayer),
            "Actual non-fill geometry keeps the existing ByLayer policy");
        check(hashes.All(pair => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value),
            "Actual Revit native DWGs remain unchanged");
        File.WriteAllText(Path.Combine(output, "fill-appearance-result.json"), JsonSerializer.Serialize(new
        {
            response.PreservedFillColors, response.PreservedMaskingEntities, response.NormalizedEntityColors,
            hatchCount = hatches.Length, patternedHatchCount = hatches.Count(h => !h.IsSolid),
            colors = hatches.GroupBy(h => (h.Color.R, h.Color.G, h.Color.B)).Select(group => new { group.Key.R, group.Key.G, group.Key.B, count = group.Count() }),
            patterns = hatches.GroupBy(h => new { h.IsSolid, name = h.Pattern?.Name, h.PatternScale, h.PatternAngle })
                .Select(group => new { group.Key, count = group.Count() })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
