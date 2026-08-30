using System.Text.Json;
using Autodesk.Revit.DB;
using ChangExport.UI;

namespace ChangExport.Export;

public sealed class RevitDwgExportService
{
    public ExportRunResult Export(
        Document document,
        IReadOnlyList<ExportSheetChoice> choices,
        string outputFolder,
        string setupName,
        string profileName,
        string arrangeMode,
        int columns,
        double margin)
    {
        Directory.CreateDirectory(outputFolder);
        var result = new ExportRunResult { OutputFolder = outputFolder };

        DWGExportOptions options = string.IsNullOrWhiteSpace(setupName) || setupName.StartsWith("(")
            ? new DWGExportOptions()
            : DWGExportOptions.GetPredefinedOptions(document, setupName);
        options.MergedViews = true;

        foreach (ExportSheetChoice choice in choices)
        {
            ViewSheet? sheet = document.GetElement(new ElementId(choice.ElementId)) as ViewSheet;
            if (sheet is null)
            {
                result.Items.Add(new ExportItemResult(choice.SheetNumber, choice.SheetName, false, "Sheet를 찾을 수 없습니다."));
                continue;
            }

            string group = string.IsNullOrWhiteSpace(choice.Group) ? "미지정" : choice.Group;
            string groupDirectory = Path.Combine(outputFolder, SanitizeFileName(group));
            Directory.CreateDirectory(groupDirectory);
            string baseName = MakeUniqueBaseName(groupDirectory,
                $"{choice.Order:000}_{SanitizeFileName(choice.SheetNumber)}_{SanitizeFileName(choice.SheetName)}");

            try
            {
                bool success = document.Export(
                    groupDirectory,
                    baseName,
                    new List<ElementId> { sheet.Id },
                    options);
                result.Items.Add(new ExportItemResult(choice.SheetNumber, choice.SheetName, success,
                    success ? Path.Combine(group, baseName + ".dwg") : "Revit Export가 false를 반환했습니다."));
            }
            catch (Exception ex)
            {
                result.Items.Add(new ExportItemResult(choice.SheetNumber, choice.SheetName, false, ex.Message));
            }
        }

        string manifestPath = Path.Combine(outputFolder, $"ChangExport_Manifest_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        var manifest = new
        {
            jobId = Guid.NewGuid(),
            executedAt = DateTimeOffset.Now,
            modelPath = document.PathName,
            revitVersion = document.Application.VersionNumber,
            addinVersion = "Beta 0.1.0",
            profile = profileName,
            exportSetup = setupName,
            prototypeMode = "Native DWG per Sheet",
            requestedArrangement = new { mode = arrangeMode, columns, marginMm = margin, applied = false },
            postProcessor = "Not connected in Beta 0.1.0",
            items = result.Items
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        result.ManifestPath = manifestPath;
        return result;
    }

    private static string MakeUniqueBaseName(string directory, string requested)
    {
        string candidate = requested;
        int version = 2;
        while (Directory.EnumerateFiles(directory, candidate + "*.dwg").Any())
            candidate = requested + $"_v{version++}";
        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        string result = value.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return string.IsNullOrWhiteSpace(result) ? "미지정" : result;
    }
}

public sealed class ExportRunResult
{
    public string OutputFolder { get; init; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public List<ExportItemResult> Items { get; } = new();
    public int SuccessCount => Items.Count(x => x.Success);
    public int FailedCount => Items.Count(x => !x.Success);
}

public sealed record ExportItemResult(string SheetNumber, string SheetName, bool Success, string Message);
