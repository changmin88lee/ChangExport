using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChangExport.Models;

namespace ChangExport.Standards;

public sealed class CadStandardRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ProfileDirectory { get; }
    public string ActiveProfilePath => Path.Combine(ProfileDirectory, "Company_Default.json");

    public CadStandardRepository()
    {
        ProfileDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChangExport",
            "Profiles");
    }

    public CadStandardProfile LoadActive()
    {
        EnsureDefaultProfile();
        string json = File.ReadAllText(ActiveProfilePath);
        return JsonSerializer.Deserialize<CadStandardProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("레이어 설정을 읽을 수 없습니다.");
    }

    public void SaveActive(CadStandardProfile profile)
    {
        Directory.CreateDirectory(ProfileDirectory);
        string tempPath = ActiveProfilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(profile, JsonOptions));
        File.Move(tempPath, ActiveProfilePath, true);
    }

    public IReadOnlyList<string> Validate(CadStandardProfile profile)
    {
        var issues = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        char[] invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=' }).Distinct().ToArray();

        foreach (CadLayerDefinition layer in profile.Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Name))
                issues.Add("이름이 빈 Layer가 있습니다.");
            else if (layer.Name.IndexOfAny(invalid) >= 0)
                issues.Add($"금지문자가 포함된 Layer: {layer.Name}");
            else if (!names.Add(layer.Name.Trim()))
                issues.Add($"중복 Layer: {layer.Name}");

            if (layer.ColorIndex is < 1 or > 255)
                issues.Add($"ACI 색상 범위 오류: {layer.Name} = {layer.ColorIndex}");
            if (layer.LineweightMm <= 0)
                issues.Add($"선가중치 오류: {layer.Name}");
        }

        foreach (DwgRule rule in profile.Rules.Where(x => x.Enabled))
        {
            if (!names.Contains(rule.TargetLayer))
                issues.Add($"Rule '{rule.Name}'의 Target Layer가 Profile에 없습니다: {rule.TargetLayer}");
        }

        return issues;
    }

    private void EnsureDefaultProfile()
    {
        if (File.Exists(ActiveProfilePath))
            return;

        Directory.CreateDirectory(ProfileDirectory);
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        string bundledPath = Path.Combine(assemblyDirectory, "Data", "Company_Default.json");
        if (File.Exists(bundledPath))
        {
            File.Copy(bundledPath, ActiveProfilePath, false);
            return;
        }

        SaveActive(CreateFallbackProfile());
    }

    private static CadStandardProfile CreateFallbackProfile() => new()
    {
        Layers =
        {
            new() { Name = "A-WALL-CONC", ColorIndex = 8, LineweightMm = 0.25, Description = "콘크리트 벽" },
            new() { Name = "A-WALL-BRICK", ColorIndex = 1, LineweightMm = 0.18, Description = "조적벽" },
            new() { Name = "S-BEAM", ColorIndex = 3, LineweightMm = 0.30, Description = "구조 보" }
        }
    };
}
