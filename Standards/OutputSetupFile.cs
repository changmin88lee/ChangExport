using System.Text.Json;
using ChangExport.Models;

namespace ChangExport.Standards;

public sealed class OutputSetupFile
{
    public string Format { get; set; } = "ChangExport.OutputSetup";
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "기본값";
    public List<RevitLayerRow> Layers { get; set; } = new();

    public void Validate()
    {
        if (Format != "ChangExport.OutputSetup" || Version != 1 || string.IsNullOrWhiteSpace(Name) || Layers == null || Layers.Count == 0)
            throw new InvalidDataException("창Export 출력 설정 파일이 아니거나 항목이 없습니다.");
        if (Layers.Any(r => r == null || string.IsNullOrWhiteSpace(r.Category) || r.CategoryGroup is "Imported" or "Modifier")
            || Layers.GroupBy(r => r.Key).Any(g => g.Count() > 1))
            throw new InvalidDataException("중복 항목 또는 가져온 CAD 항목이 포함된 출력 설정입니다.");
        var full = Layers.Select(r => { var row = r.Copy(); row.OriginalLayer = row.OriginalCutLayer = null; row.OriginalColor = row.OriginalCutColor = null; return row; }).ToList();
        var issues = RevitLayerMappingService.Validate(full);
        if (issues.Count > 0) throw new InvalidDataException(string.Join("\n", issues.Take(12)));
    }
    public static OutputSetupFile Load(string path)
    {
        string json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(Format), out var format) || format.GetString() != "ChangExport.OutputSetup")
            throw new InvalidDataException("창Export 전용 출력 설정 파일을 선택하세요.");
        var result = JsonSerializer.Deserialize<OutputSetupFile>(json)
            ?? throw new InvalidDataException("출력 설정 파일을 읽을 수 없습니다.");
        result.Validate(); return result;
    }
    public void Save(string path)
    {
        Validate(); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
