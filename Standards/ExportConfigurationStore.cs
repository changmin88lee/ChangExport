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
        if (config.SchemaVersion is not (1 or 2) || config.Setups is null || config.SheetSets is null)
            throw new InvalidDataException("지원하지 않는 출력 설정입니다. 기존 파일은 유지됩니다.");
        if (config.OutputSetups == null || config.OutputSetups.Any(s => s == null || s.SetupName == null || s.Layers == null)
            || config.OutputSetups.GroupBy(s => s.SetupName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("창Export 출력 설정 목록을 읽을 수 없습니다. 기존 파일은 유지됩니다.");
        if (config.SchemaVersion == 1)
        {
            // Adopt the requested touching-sheet default once, without writing during Load.
            // Later spacing choices are kept by schema 2, including an explicit 10000 mm.
            foreach (var set in config.SheetSets) set.MarginMm = 0;
            config.SchemaVersion = 2;
        }
        return config;
    }

    public void Save(RevitExportConfiguration config)
    {
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
