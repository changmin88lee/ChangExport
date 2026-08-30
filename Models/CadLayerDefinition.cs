namespace ChangExport.Models;

public sealed class CadLayerDefinition
{
    public string Name { get; set; } = string.Empty;
    public int ColorIndex { get; set; } = 7;
    public string Linetype { get; set; } = "Continuous";
    public double LineweightMm { get; set; } = 0.18;
    public bool Plot { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public override string ToString() => Name;
}
