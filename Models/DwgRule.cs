namespace ChangExport.Models;

public enum RuleOperator
{
    Equals,
    Contains,
    StartsWith,
    IsEmpty
}

public sealed class DwgRule
{
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public string Category { get; set; } = string.Empty;
    public string ParameterName { get; set; } = "Type Name";
    public RuleOperator Operator { get; set; } = RuleOperator.Contains;
    public string CompareValue { get; set; } = string.Empty;
    public string TargetLayer { get; set; } = string.Empty;
}
