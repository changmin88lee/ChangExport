namespace ChangExport.Models;

public sealed class CadStandardProfile
{
    public string ProfileName { get; set; } = "한국형 DWG 기본";
    public int SchemaVersion { get; set; } = 1;
    public string RuleSetVersion { get; set; } = "1.0";
    public List<CadLayerDefinition> Layers { get; set; } = new();
    public List<DwgRule> Rules { get; set; } = new();
}
