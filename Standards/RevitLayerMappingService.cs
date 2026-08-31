using Autodesk.Revit.DB;
using ChangExport.DwgProcessing;
using ChangExport.Models;

namespace ChangExport.Standards;

public sealed class RevitLayerMappingService
{
    private readonly Document _document;
    public RevitLayerMappingService(Document document) => _document = document;
    public IReadOnlyList<string> SetupNames => new[] { string.Empty }
        .Concat(BaseExportOptions.GetPredefinedSetupNames(_document)).ToList();

    public DWGExportOptions CreateOptions(string setupName) => string.IsNullOrEmpty(setupName)
        ? new DWGExportOptions()
        : DWGExportOptions.GetPredefinedOptions(_document, setupName);

    public List<RevitLayerRow> Read(string setupName, RevitExportConfiguration config)
    {
        using DWGExportOptions options = CreateOptions(setupName);
        using ExportLayerTable table = options.GetExportLayerTable();
        var rows = new List<RevitLayerRow>();
        var saved = config.Setups.FirstOrDefault(s => s.SetupName == setupName)?.Layers
            .ToDictionary(r => r.Key) ?? new Dictionary<string, RevitLayerRow>();
        foreach (var pair in table)
        {
            ExportLayerKey key = pair.Key;
            ExportLayerInfo value = pair.Value;
            var row = new RevitLayerRow
            {
                Category = key.CategoryName, Subcategory = key.SubCategoryName, SpecialType = (int)key.SpecialType,
                CategoryGroup = value.CategoryType.ToString(),
                Layer = value.LayerName, Color = value.ColorNumber, CutLayer = value.CutLayerName, CutColor = value.CutColorNumber,
                OriginalLayer = value.LayerName, OriginalColor = value.ColorNumber,
                OriginalCutLayer = value.CutLayerName, OriginalCutColor = value.CutColorNumber
            };
            if (saved.TryGetValue(row.Key, out RevitLayerRow? edit))
            {
                if (edit.Layer != edit.OriginalLayer) row.Layer = edit.Layer;
                if (edit.Color != edit.OriginalColor) row.Color = edit.Color;
                if (edit.CutLayer != edit.OriginalCutLayer) row.CutLayer = edit.CutLayer;
                if (edit.CutColor != edit.OriginalCutColor) row.CutColor = edit.CutColor;
                row.Linetype = edit.Linetype;
                row.Lineweight = edit.Lineweight;
            }
            rows.Add(row);
        }
        return rows.OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Subcategory.Length == 0 ? 0 : 1)
            .ThenBy(r => r.Subcategory, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.SpecialType).ToList();
    }

    public DWGExportOptions Apply(string setupName, IReadOnlyList<RevitLayerRow> rows)
    {
        IReadOnlyList<string> issues = Validate(rows);
        if (issues.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, issues.Take(12)));
        DWGExportOptions options = CreateOptions(setupName);
        using ExportLayerTable table = options.GetExportLayerTable();
        foreach (RevitLayerRow row in rows.Where(r => r.HasChanges))
        {
            using var key = new ExportLayerKey(row.Category, row.Subcategory, (SpecialType)row.SpecialType);
            ExportLayerInfo value = table[key];
            value.LayerName = row.Layer;
            value.ColorNumber = row.Color;
            value.CutLayerName = row.CutLayer;
            value.CutColorNumber = row.CutColor;
            table[key] = value;
        }
        options.SetExportLayerTable(table);
        return options;
    }

    public static IReadOnlyList<string> Validate(IEnumerable<RevitLayerRow> rows)
    {
        var issues = new List<string>();
        var appearances = new Dictionary<string, LayerAppearance>(StringComparer.OrdinalIgnoreCase);
        foreach (RevitLayerRow row in rows.Where(r => r.HasChanges))
        {
            if (row.Layer != row.OriginalLayer && !ValidLayerName(row.Layer)) issues.Add($"{row.Category}: 투영 레이어 이름을 확인하세요.");
            if (row.CutLayer != row.OriginalCutLayer && !ValidLayerName(row.CutLayer)) issues.Add($"{row.Category}: 절단 레이어 이름을 확인하세요.");
            if (row.Color != row.OriginalColor && row.Color is < 1 or > 255) issues.Add($"{row.Category}: 색상 번호는 1~255입니다.");
            if (row.CutColor != row.OriginalCutColor && row.CutColor is < 1 or > 255) issues.Add($"{row.Category}: 절단 색상 번호는 1~255입니다.");
            if (row.Lineweight.HasValue && !ValidLineweights.Contains(row.Lineweight.Value)) issues.Add($"{row.Category}: 지원하지 않는 선가중치입니다.");
            foreach (string layer in new[] { row.Layer, row.CutLayer }.Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (row.Linetype.Length == 0 && !row.Lineweight.HasValue) continue;
                var style = new LayerAppearance { Layer = layer, Linetype = row.Linetype, Lineweight = row.Lineweight };
                if (appearances.TryGetValue(layer, out var previous) && (previous.Linetype != style.Linetype || previous.Lineweight != style.Lineweight))
                    issues.Add($"{layer}: 같은 DWG 레이어에 서로 다른 선종류/선가중치가 지정되었습니다.");
                appearances[layer] = style;
            }
        }
        return issues.Distinct().ToList();
    }

    public static List<LayerAppearance> GetAppearances(IEnumerable<RevitLayerRow> rows) => rows
        .Where(r => r.Linetype.Length > 0 || r.Lineweight.HasValue)
        .SelectMany(r => new[] { r.Layer, r.CutLayer }.Where(l => l.Length > 0)
            .Select(l => new LayerAppearance { Layer = l, Linetype = r.Linetype, Lineweight = r.Lineweight }))
        .GroupBy(r => r.Layer, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    public static readonly int[] ValidLineweights = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
    private static bool ValidLayerName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255
        && value.IndexOfAny("<>/\\\":;?*|=\r\n".ToCharArray()) < 0;
}
