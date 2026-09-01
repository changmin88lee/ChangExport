using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using ChangExport.Models;

namespace ChangExport.Standards;

public sealed class ExportConfigurationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string FilePath { get; }

    public ExportConfigurationStore(string filePath) => FilePath = filePath;

    public static ExportConfigurationStore ForDocument(Document document)
    {
        string identity = document.ProjectInformation.UniqueId + "|" + document.PathName + "|" + document.Title;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new ExportConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChangExport", "ExportSettings", hash + ".json"));
    }

    public RevitExportConfiguration Load()
    {
        if (!File.Exists(FilePath)) return new();
        var config = JsonSerializer.Deserialize<RevitExportConfiguration>(File.ReadAllText(FilePath), Json)
            ?? throw new InvalidDataException("출력 설정 파일을 읽을 수 없습니다. 기존 파일은 유지됩니다.");
        if (config.SchemaVersion is not (1 or 2 or 3 or 4) || config.Setups is null || config.SheetSets is null || config.WideLineKeyword == null)
            throw new InvalidDataException("지원하지 않는 출력 설정입니다. 기존 파일은 유지됩니다.");
        if (config.OutputSetups == null || config.OutputSetups.Any(s => s == null || s.SetupName == null || s.Layers == null
                || s.Layers.Any(r => r == null || !ViewLayerScope.IsValid(r.ViewScope, allowLegacy: true))
                || (s.MaterialRules ?? new()).Any(r => r == null || !ViewLayerScope.IsValid(r.ViewScope, allowLegacy: true)))
            || config.OutputSetups.GroupBy(s => s.SetupName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("창Export 출력 설정 목록을 읽을 수 없습니다. 기존 파일은 유지됩니다.");
        if (config.SchemaVersion == 1)
        {
            // Adopt the requested touching-sheet default once, without writing during Load.
            // Later spacing choices are kept by schema 2, including an explicit 10000 mm.
            foreach (var set in config.SheetSets) set.MarginMm = 0;
            config.SchemaVersion = 2;
        }
        foreach (var setup in config.OutputSetups) setup.MaterialRules ??= new();
        if (config.SchemaVersion == 2) config.SchemaVersion = 3;
        if (config.SchemaVersion == 3) MigrateViewScopes(config);
        config.SheetTemplateIds ??= new();
        foreach (var setup in config.OutputSetups)
        {
            if (string.IsNullOrWhiteSpace(setup.SetupId)) setup.SetupId = Guid.NewGuid().ToString("N");
            foreach (var row in setup.Layers) row.ViewScope = string.Empty;
            foreach (var rule in setup.MaterialRules) rule.ViewScope = string.Empty;
        }
        if (config.OutputSetups.GroupBy(s => s.SetupId, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidDataException("중복된 DWG 레이어 템플릿 식별자가 있습니다. 기존 파일은 유지됩니다.");
        return config;
    }

    private static void MigrateViewScopes(RevitExportConfiguration config)
    {
        var migrated = new List<ExportSetupEdits>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in config.OutputSetups)
        {
            bool scoped = source.Layers.Any(r => !string.IsNullOrEmpty(r.ViewScope))
                || source.MaterialRules.Any(r => !string.IsNullOrEmpty(r.ViewScope));
            if (!scoped)
            {
                source.SetupId = Guid.NewGuid().ToString("N"); usedNames.Add(source.SetupName); migrated.Add(source); continue;
            }
            ExportSetupEdits Slice(string scope, string name)
            {
                var layers = source.Layers.Where(r => string.IsNullOrEmpty(r.ViewScope) || r.ViewScope == scope)
                    .Select(r => { var copy = r.Copy(); copy.ViewScope = ""; return copy; })
                    .GroupBy(r => r.Key).Select(g => g.Last()).ToList();
                var materials = source.MaterialRules.Where(r => string.IsNullOrEmpty(r.ViewScope) || r.ViewScope == scope)
                    .Select(r => { var copy = r.Copy(); copy.ViewScope = ""; return copy; })
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.MaterialUniqueId) ? r.RuleId : r.MaterialUniqueId).Select(g => g.Last()).ToList();
                return new ExportSetupEdits { SetupName = name, Layers = layers, MaterialRules = materials };
            }
            string Unique(string basis)
            {
                string name = basis; for (int index = 2; !usedNames.Add(name); index++) name = basis + " " + index;
                return name;
            }
            string baseName = source.SetupName; usedNames.Add(baseName);
            var slices = new[] { (ViewLayerScope.ArchitecturePlan, "건축평면도"), (ViewLayerScope.StructuralPlan, "구조평면도"), (ViewLayerScope.CeilingPlan, "천장평면도") }
                .Select(entry => (entry.Item1, entry.Item2, Value: Slice(entry.Item1, ""))).ToList();
            var first = slices.FirstOrDefault(s => s.Item1 == ViewLayerScope.ArchitecturePlan && (s.Value.Layers.Count > 0 || s.Value.MaterialRules.Count > 0));
            if (first.Value == null) first = slices.First(s => s.Value.Layers.Count > 0 || s.Value.MaterialRules.Count > 0);
            first.Value.SetupName = baseName; migrated.Add(first.Value);
            string Signature(ExportSetupEdits value) => JsonSerializer.Serialize(new { value.Layers, value.MaterialRules }, Json);
            string baseline = Signature(first.Value);
            foreach (var candidate in slices.Where(s => !ReferenceEquals(s.Value, first.Value)))
            {
                if (candidate.Value.Layers.Count == 0 && candidate.Value.MaterialRules.Count == 0 || Signature(candidate.Value) == baseline) continue;
                candidate.Value.SetupName = Unique((baseName.Length == 0 ? "기본값" : baseName) + "-" + candidate.Item2); migrated.Add(candidate.Value);
            }
        }
        config.OutputSetups = migrated.Count == 0 ? new() { new() } : migrated;
        config.SchemaVersion = 4;
        config.SheetTemplateIds = new();
        foreach (var set in config.SheetSets) set.TemplateId = string.Empty;
    }

    public void Save(RevitExportConfiguration config)
    {
        config.SchemaVersion = 4;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, Json));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
