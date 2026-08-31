namespace ChangExport.Models;

public sealed class RevitExportConfiguration
{
    public int SchemaVersion { get; set; } = 2;
    public string SelectedSetup { get; set; } = string.Empty;
    public List<ExportSetupEdits> Setups { get; set; } = new();
    public List<SheetSetDefinition> SheetSets { get; set; } = new();
}

public sealed class ExportSetupEdits
{
    public string SetupName { get; set; } = string.Empty;
    public List<RevitLayerRow> Layers { get; set; } = new();
}

public sealed class RevitLayerRow
{
    public bool IsCustom { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string TypeNameContains { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Subcategory { get; set; } = string.Empty;
    public int SpecialType { get; set; }
    public string CategoryGroup { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public int Color { get; set; }
    public string CutLayer { get; set; } = string.Empty;
    public int CutColor { get; set; }
    // Null means inherit the current Revit setup; changes are stored separately from the source setup.
    public string? OriginalLayer { get; set; }
    public int? OriginalColor { get; set; }
    public string? OriginalCutLayer { get; set; }
    public int? OriginalCutColor { get; set; }
    public string Linetype { get; set; } = string.Empty;
    public int? Lineweight { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int LineweightChoice { get => Lineweight ?? -1; set => Lineweight = value < 0 ? null : value; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => IsCustom ? "    └ 필터: 유형 이름 포함" : string.IsNullOrEmpty(Subcategory) ? Category : "    └ " + Subcategory;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasChanges => IsCustom || Layer != OriginalLayer || Color != OriginalColor || CutLayer != OriginalCutLayer
        || CutColor != OriginalCutColor || Linetype.Length > 0 || Lineweight.HasValue;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => IsCustom ? "rule:" + RuleId : $"{Category}\u001f{Subcategory}\u001f{SpecialType}";
    public bool Matches(string category, string typeName) => IsCustom && Category == category
        && !string.IsNullOrWhiteSpace(TypeNameContains) && typeName.Contains(TypeNameContains, StringComparison.OrdinalIgnoreCase);
    public RevitLayerRow Copy() => (RevitLayerRow)MemberwiseClone();
}

public sealed class SheetSetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<string> SheetUniqueIds { get; set; } = new();
    public string Direction { get; set; } = "Horizontal";
    public double MarginMm { get; set; } = 0;
    public SheetSetDefinition Copy() => new()
    {
        Id = Id, Name = Name, SheetUniqueIds = SheetUniqueIds.ToList(), Direction = Direction, MarginMm = MarginMm
    };
}

public sealed record SheetDescriptor(string UniqueId, long ElementId, string Number, string Name);
