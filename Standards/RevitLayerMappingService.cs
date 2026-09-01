using Autodesk.Revit.DB;
using ChangExport.DwgProcessing;
using ChangExport.Models;

namespace ChangExport.Standards;

public sealed class RevitLayerMappingService
{
    // Revit does not plot the built-in <Invisible Lines> style. Give that row a
    // private native-export layer so the managed stage can remove it without
    // deleting unrelated entities that a user mapped to the same final layer.
    internal const string InvisibleLineExportLayer = "CE__INVISIBLE_LINES__DO_NOT_EXPORT";

    private readonly Document _document;
    private List<RevitLayerRow>? _catalog;
    public RevitLayerMappingService(Document document) => _document = document;
    public static IReadOnlyList<string> SetupNames(RevitExportConfiguration config) => new[] { string.Empty }
        .Concat(config.OutputSetups.Select(s => s.SetupName).Where(n => n.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<MaterialLayerRule> ReadMaterialRules(string setupName, RevitExportConfiguration config,
        string viewScope = ViewLayerScope.ArchitecturePlan) => config.OutputSetups.FirstOrDefault(s => s.SetupName == setupName)?.MaterialRules?
        .Where(r => string.IsNullOrEmpty(r.ViewScope) || r.ViewScope == viewScope)
        .Select(r => { var copy = r.Copy(); copy.ViewScope = viewScope; return copy; }).ToList() ?? new();

    public DWGExportOptions CreateOptions(string setupName) => new()
    {
        LayerMapping = "AIA",
        Colors = ExportColorMode.TrueColorPerView,
        PropOverrides = PropOverrideMode.ByEntity
    };

    public List<RevitLayerRow> Read(string setupName, RevitExportConfiguration config, string viewScope = ViewLayerScope.ArchitecturePlan)
    {
        _catalog ??= RevitCategoryCatalog.Read(_document);
        var savedRows = config.OutputSetups.FirstOrDefault(s => s.SetupName == setupName)?.Layers
            .Where(r => string.IsNullOrEmpty(r.ViewScope) || r.ViewScope == viewScope)
            .Select(r => { var copy = r.Copy(); copy.ViewScope = viewScope; return copy; }).ToList() ?? new();
        var catalog = _catalog.Select(r => { var copy = r.Copy(); copy.ViewScope = viewScope; return copy; }).ToList();
        var result = MergeCatalog(catalog, savedRows);
        foreach (var row in result) row.ViewScope = viewScope;
        return result;
    }

    public static List<RevitLayerRow> MergeCatalog(IEnumerable<RevitLayerRow> catalog, IReadOnlyList<RevitLayerRow> savedRows)
    {
        var rows = new List<RevitLayerRow>();
        var used = new HashSet<string>();
        var byKey = savedRows.Where(r => !r.IsCustom).ToDictionary(r => r.Key);
        var byCategory = savedRows.Where(r => !r.IsCustom && r.CategoryId < 0 && r.Subcategory.Length == 0)
            .GroupBy(r => (r.CategoryId, r.SpecialType)).ToDictionary(g => g.Key, g => g.First());
        foreach (var baseline in catalog)
        {
            var edit = byKey.GetValueOrDefault(baseline.Key)
                ?? (baseline.Subcategory.Length == 0 ? byCategory.GetValueOrDefault((baseline.CategoryId, baseline.SpecialType)) : null);
            var row = baseline.Copy();
            if (edit != null)
            {
                row.Layer = edit.Layer; row.Color = edit.Color; row.CutLayer = edit.CutLayer; row.CutColor = edit.CutColor;
                row.Linetype = edit.Linetype; row.Lineweight = edit.Lineweight; used.Add(edit.Key);
            }
            rows.Add(row);
        }
        rows.AddRange(savedRows.Where(r => !used.Contains(r.Key) && r.CategoryGroup is not ("Imported" or "Modifier")).Select(r => r.Copy()));
        return RevitCategoryCatalog.Order(rows);
    }

    public DWGExportOptions Apply(string setupName, IReadOnlyList<RevitLayerRow> rows)
    {
        new OutputSetupFile { Name = setupName.Length == 0 ? "기본값" : setupName, Layers = rows.ToList() }.Validate();
        IReadOnlyList<string> issues = Validate(rows);
        if (issues.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, issues.Take(12)));
        DWGExportOptions options = CreateOptions(setupName);
        using ExportLayerTable table = options.GetExportLayerTable();
        foreach (RevitLayerRow row in rows.Where(r => !r.IsCustom))
        {
            using var key = new ExportLayerKey(row.Category, row.Subcategory, (SpecialType)row.SpecialType);
            bool exists = table.ContainsKey(key);
            using ExportLayerInfo value = exists ? table[key] : new ExportLayerInfo();
            value.CategoryType = Enum.TryParse<LayerCategoryType>(row.CategoryGroup, out var group) ? group : LayerCategoryType.Model;
            bool invisible = IsInvisibleLineRow(row);
            value.LayerName = invisible ? InvisibleLineExportLayer : row.Layer;
            value.ColorNumber = invisible ? 7 : row.Color;
            value.CutLayerName = invisible ? InvisibleLineExportLayer : row.CutLayer;
            value.CutColorNumber = invisible ? 7 : row.CutColor;
            using var modifier = new LayerModifier(ModifierType.Category, "");
            value.SetLayerModifiers(new List<LayerModifier> { modifier });
            value.SetCutLayerModifiers(new List<LayerModifier> { modifier });
            if (exists) table[key] = value; else table.Add(key, value);
        }
        options.SetExportLayerTable(table);
        return options;
    }

    internal static List<string> InternalExcludedLayers(IEnumerable<RevitLayerRow> rows) =>
        rows.Any(IsInvisibleLineRow) ? new List<string> { InvisibleLineExportLayer } : new List<string>();

    internal static bool IsInvisibleLineRow(RevitLayerRow row)
    {
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        string name = Normalize(string.IsNullOrWhiteSpace(row.Subcategory) ? row.Category : row.Subcategory);
        return name is "보이지않는선" or "invisiblelines" or "invisibleline";
    }

    public static IReadOnlyList<string> Validate(IEnumerable<RevitLayerRow> rows)
    {
        var all = rows.ToList();
        var issues = new List<string>();
        if (all.Any(r => !ViewLayerScope.IsValid(r.ViewScope, allowLegacy: true))) issues.Add("지원하지 않는 평면도 구분이 있습니다.");
        var appearances = new Dictionary<string, LayerAppearance>(StringComparer.OrdinalIgnoreCase);
        foreach (RevitLayerRow row in all.Where(r => r.HasChanges))
        {
            if (row.IsCustom)
            {
                if (string.IsNullOrWhiteSpace(row.TypeNameContains)) issues.Add($"{row.Category}: 필터의 포함 문자를 입력하세요.");
                if (string.IsNullOrWhiteSpace(row.RuleId)) issues.Add($"{row.Category}: 필터 식별자가 없습니다. 필터를 다시 추가하세요.");
                if (!ValidLayerName(row.Layer) || !ValidLayerName(row.CutLayer)) issues.Add($"{row.Category}: 필터의 투영/절단 레이어를 입력하세요.");
                if (row.Color is < 1 or > 255 || row.CutColor is < 1 or > 255) issues.Add($"{row.Category}: 필터 색상은 1~255입니다.");
            }
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
        if (all.Where(r => r.IsCustom).GroupBy(r => (r.ViewScope, r.RuleId)).Any(g => g.Count() > 1)) issues.Add("중복된 필터 식별자가 있습니다.");
        foreach (var group in all.SelectMany(r => new[] { (r.Layer, r.Color, r.IsCustom), (r.CutLayer, r.CutColor, r.IsCustom) })
            .Where(v => !string.IsNullOrWhiteSpace(v.Item1) && v.Item2 is >= 1 and <= 255).GroupBy(v => v.Item1, StringComparer.OrdinalIgnoreCase))
            if (group.Select(v => v.Item2).Distinct().Count() > 1)
                issues.Add($"{group.Key}: 같은 레이어에 다른 색상을 지정했습니다. 색상을 통일하거나 다른 레이어 이름을 사용하세요.");
        return issues.Distinct().ToList();
    }

    public static List<string> ValidateMaterialRules(IEnumerable<MaterialLayerRule> rules)
    {
        var all = rules.ToList();
        var issues = new List<string>();
        if (all.Any(r => !ViewLayerScope.IsValid(r.ViewScope, allowLegacy: true))) issues.Add("재료 필터에 지원하지 않는 평면도 구분이 있습니다.");
        foreach (var rule in all)
        {
            if (string.IsNullOrWhiteSpace(rule.MaterialUniqueId)) issues.Add("재료 필터에서 Revit 재료를 선택하세요.");
            if (!ValidLayerName(rule.Layer)) issues.Add($"{rule.Caption}: 재료 CAD 레이어 이름을 확인하세요.");
            if (rule.Color is < 1 or > 255) issues.Add($"{rule.Caption}: 재료 레이어 색상은 1~255입니다.");
        }
        if (all.Where(r => r.MaterialUniqueId.Length > 0).GroupBy(r => (r.ViewScope, r.MaterialUniqueId)).Any(g => g.Count() > 1))
            issues.Add("같은 Revit 재료가 재료 필터에 두 번 지정되었습니다.");
        if (all.GroupBy(r => (r.ViewScope, r.RuleId)).Any(g => g.Count() > 1)) issues.Add("중복된 재료 필터 식별자가 있습니다.");
        foreach (var group in all.Where(r => !string.IsNullOrWhiteSpace(r.Layer) && r.Color is >= 1 and <= 255)
            .GroupBy(r => r.Layer, StringComparer.OrdinalIgnoreCase))
            if (group.Select(r => r.Color).Distinct().Count() > 1)
                issues.Add($"{group.Key}: 같은 재료 CAD 레이어에 다른 색상을 지정했습니다.");
        return issues.Distinct().ToList();
    }

    public static List<string> ValidateCombined(IEnumerable<RevitLayerRow> rows, IEnumerable<MaterialLayerRule> materialRules)
    {
        var issues = new List<string>();
        var colors = rows.SelectMany(r => new[] { (Layer: r.Layer, Color: r.Color), (Layer: r.CutLayer, Color: r.CutColor) })
            .Concat(materialRules.Select(r => (r.Layer, r.Color)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Layer) && item.Color is >= 1 and <= 255);
        foreach (var group in colors.GroupBy(item => item.Layer, StringComparer.OrdinalIgnoreCase))
            if (group.Select(item => item.Color).Distinct().Count() > 1)
                issues.Add($"{group.Key}: 카테고리·유형·재료 설정에서 같은 DWG 레이어에 다른 색상을 지정했습니다.");
        return issues;
    }

    public static List<LayerAppearance> GetAppearances(IEnumerable<RevitLayerRow> rows) => rows
        .SelectMany(r => new[] { (Layer: r.Layer, Color: r.Color), (Layer: r.CutLayer, Color: r.CutColor) }.Where(l => l.Layer.Length > 0)
            .Select(l => new LayerAppearance { Layer = l.Layer, Color = l.Color, Linetype = r.Linetype, Lineweight = r.Lineweight }))
        .GroupBy(r => r.Layer, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    public static readonly int[] ValidLineweights = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
    private static bool ValidLayerName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255
        && value.IndexOfAny("<>/\\\":;?*|=\r\n".ToCharArray()) < 0;
}
