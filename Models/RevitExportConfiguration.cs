namespace ChangExport.Models;

public sealed class RevitExportConfiguration
{
    public int SchemaVersion { get; set; } = 7;
    public string SelectedSetup { get; set; } = string.Empty;
    public List<ExportSetupEdits> Setups { get; set; } = new();
    // Old Revit setup edits remain in Setups for preservation; they are not auto-imported.
    public string SelectedOutputSetup { get; set; } = string.Empty;
    public string WideLineKeyword { get; set; } = "##";
    public double SheetSpacingMm { get; set; }
    public List<ExportSetupEdits> OutputSetups { get; set; } = new() { new() };
    public List<SheetSetDefinition> SheetSets { get; set; } = new();
    public Dictionary<string, string> SheetTemplateIds { get; set; } = new();
    public List<string> SheetSourceOrder { get; set; } = new();
}

public sealed class ExportSetupEdits
{
    public string SetupId { get; set; } = Guid.NewGuid().ToString("N");
    public string SetupName { get; set; } = string.Empty;
    public List<RevitLayerRow> Layers { get; set; } = new();
    public List<MaterialLayerRule> MaterialRules { get; set; } = new();
}

public sealed class MaterialLayerRule
{
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
    public string MaterialUniqueId { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string ViewScope { get; set; } = ViewLayerScope.ArchitecturePlan;
    public string Layer { get; set; } = string.Empty;
    public int Color { get; set; } = 7;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => string.IsNullOrWhiteSpace(MaterialName) ? "재료를 선택하세요" : MaterialName;
    public MaterialLayerRule Copy() => (MaterialLayerRule)MemberwiseClone();
}

public static class ViewLayerScope
{
    public const string ArchitecturePlan = "ArchitecturePlan";
    public const string StructuralPlan = "StructuralPlan";
    public const string CeilingPlan = "CeilingPlan";
    public static readonly string[] All = { ArchitecturePlan, StructuralPlan, CeilingPlan };
    public static bool IsValid(string? value, bool allowLegacy = false) => (allowLegacy && string.IsNullOrEmpty(value))
        || value is ArchitecturePlan or StructuralPlan or CeilingPlan;
    public static string Label(string value) => value switch
    {
        StructuralPlan => "구조평면도",
        CeilingPlan => "천장평면도",
        _ => "건축평면도"
    };
}

public sealed record MaterialChoice(string UniqueId, long ElementId, string Name)
{
    public override string ToString() => Name;
}

public sealed record LayerTemplateChoice(string Id, string Name)
{
    public override string ToString() => Name.Length == 0 ? "기본값" : Name;
}

public sealed class RevitLayerRow
{
    public long? CategoryId { get; set; }
    public long? SubcategoryId { get; set; }
    public bool IsCustom { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string TypeNameContains { get; set; } = string.Empty;
    public string ViewScope { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Subcategory { get; set; } = string.Empty;
    public int SpecialType { get; set; } = -1;
    public string CategoryGroup { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public int Color { get; set; }
    public string CutLayer { get; set; } = string.Empty;
    public int CutColor { get; set; }
    // Baseline values belong to the independent ChangExport catalog.
    public string? OriginalLayer { get; set; }
    public int? OriginalColor { get; set; }
    public string? OriginalCutLayer { get; set; }
    public int? OriginalCutColor { get; set; }
    private string _linetype = string.Empty;
    public string Linetype { get => _linetype; set => _linetype = value ?? string.Empty; }
    public int? Lineweight { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int LineweightChoice { get => Lineweight ?? -1; set => Lineweight = value < 0 ? null : value; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => IsCustom ? "    └ 필터: 유형 이름 포함" : string.IsNullOrEmpty(Subcategory) ? Category : "    └ " + Subcategory;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasChanges => IsCustom || Layer != OriginalLayer || Color != OriginalColor || CutLayer != OriginalCutLayer
        || CutColor != OriginalCutColor || !string.IsNullOrEmpty(Linetype) || Lineweight.HasValue;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => (string.IsNullOrEmpty(ViewScope) ? "legacy" : ViewScope) + "\u001e" +
        (IsCustom ? "rule:" + RuleId : $"{Category}\u001f{Subcategory}\u001f{SpecialType}");
    public bool Matches(string category, string typeName) => IsCustom && Category == category
        && !string.IsNullOrWhiteSpace(TypeNameContains) && typeName.Contains(TypeNameContains, StringComparison.OrdinalIgnoreCase);
    public RevitLayerRow Copy() => (RevitLayerRow)MemberwiseClone();
}

public sealed class SheetSetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public List<string> SheetUniqueIds { get; set; } = new();
    public string Direction { get; set; } = "Horizontal";
    // Schema 4 compatibility only. Active output uses RevitExportConfiguration.SheetSpacingMm.
    public double MarginMm { get; set; } = 0;
    public SheetSetDefinition Copy() => new()
    {
        Id = Id, Name = Name, TemplateId = TemplateId, SheetUniqueIds = SheetUniqueIds.ToList(), Direction = Direction, MarginMm = MarginMm
    };
}

public sealed record SheetDescriptor(string UniqueId, long ElementId, string Number, string Name,
    string SourceKey = "", string SourceName = "", bool IsHost = true, string SourcePath = "", int LinkDepth = 0)
{
    // Host keys intentionally remain the historical UniqueId so every existing
    // saved set migrates without rewriting its membership.
    public string Key => IsHost || string.IsNullOrWhiteSpace(SourceKey) ? UniqueId : SourceKey + "\u001f" + UniqueId;
    public string SourceOrderKey => IsHost ? "host" : SourceKey;
    public string DisplayNumber => IsHost ? Number : $"[{SourceName}] {Number}";
    public string DisplayName => IsHost ? Name : $"[{SourceName}] {Name}";
}
