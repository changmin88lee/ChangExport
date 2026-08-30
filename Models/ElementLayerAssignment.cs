namespace ChangExport.Models;

public enum LayerDecisionSource
{
    ManualOverride,
    RuleMatch,
    DefaultMapping,
    InvalidTarget
}

public sealed class ElementLayerAssignment
{
    public long ElementId { get; init; }
    public string Category { get; init; } = string.Empty;
    public string FamilyName { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public string ManualLayer { get; init; } = string.Empty;
    public string MatchedRule { get; init; } = string.Empty;
    public string TargetLayer { get; init; } = string.Empty;
    public LayerDecisionSource Source { get; init; }
    public string Warning { get; init; } = string.Empty;
}
