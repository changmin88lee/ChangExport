using System.Text.Json;
using Autodesk.Revit.DB;
using ChangExport.App;
using ChangExport.DwgProcessing;
using ChangExport.Models;
using ChangExport.Standards;

namespace ChangExport.Export;

public sealed class RevitDwgExportService
{
    public ExportRunResult Export(Document document, IReadOnlyList<SheetSetDefinition> sets, string outputFolder,
        string setupName, IReadOnlyList<RevitLayerRow> layers, Action<string> progress, Func<bool> cancel, Action pump)
    {
        var processor = new ManagedDwgProcessor();

        Directory.CreateDirectory(outputFolder);
        string jobId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
        string staging = Path.Combine(outputFolder, "_ChangExport_Work", jobId);
        Directory.CreateDirectory(staging);
        var result = new ExportRunResult { OutputFolder = outputFolder, WorkFolder = staging };
        using DWGExportOptions options = new RevitLayerMappingService(document).Apply(setupName, layers);
        options.MergedViews = false; // Disable sheet-view/link Xrefs in staging; the saved Revit setup remains unchanged.
        options.FileVersion = ACADVersion.R2010; // Export-only override; never modify the project's saved setup.
        try
        {
            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                SheetSetDefinition set = sets[setIndex];
                var item = new ExportItemResult(set.Name, string.Empty, false, "미실행");
                result.Items.Add(item);
                try
                {
                    CheckCancel(cancel);
                    if (set.SheetUniqueIds.Count == 0 || set.SheetUniqueIds.Distinct().Count() != set.SheetUniqueIds.Count)
                        throw new InvalidDataException("비어 있거나 중복 시트가 있는 세트입니다.");
                    string setFolder = Path.Combine(staging, $"set_{setIndex + 1:000}"); Directory.CreateDirectory(setFolder);
                    var flattened = new List<string>();
                    for (int sheetIndex = 0; sheetIndex < set.SheetUniqueIds.Count; sheetIndex++)
                    {
                        CheckCancel(cancel);
                        ViewSheet sheet = document.GetElement(set.SheetUniqueIds[sheetIndex]) as ViewSheet
                            ?? throw new InvalidOperationException("프로젝트에 없는 시트입니다.");
                        string nativeDirectory = Path.Combine(setFolder, $"native_{sheetIndex + 1:000}"); Directory.CreateDirectory(nativeDirectory);
                        progress($"{setIndex + 1}/{sets.Count} 세트 · {set.Name}\n{sheetIndex + 1}/{set.SheetUniqueIds.Count} 시트 · {sheet.SheetNumber} · Revit DWG 생성");
                        bool success = document.Export(nativeDirectory, "sheet", new List<ElementId> { sheet.Id }, options);
                        if (!success || !File.Exists(Path.Combine(nativeDirectory, "sheet.dwg"))) throw new IOException("Revit이 시트 DWG를 생성하지 못했습니다.");
                        CheckCancel(cancel);
                        string flat = Path.Combine(setFolder, $"flat_{sheetIndex + 1:000}.dwg");
                        progress($"{set.Name} · {sheet.SheetNumber}\n내장 엔진 모형공간 변환 및 재열기 검사 중");
                        BridgeResponse conversion = processor.Run(new BridgeRequest { Operation = "Flatten", OutputPath = flat },
                            Path.Combine(nativeDirectory, "sheet.dwg"), setFolder, cancel, pump);
                        item.Warnings.AddRange(conversion.Warnings.Select(w => $"시트 {sheet.SheetNumber}: {w}")); flattened.Add(flat);
                    }
                    CheckCancel(cancel);
                    string finalStage = Path.Combine(setFolder, "merged.dwg");
                    progress($"{set.Name}\n{set.SheetUniqueIds.Count}장 {(set.Direction == "Vertical" ? "세로" : "가로")} 배치 · 최종 DWG 검사 중");
                    var merged = processor.Run(new BridgeRequest { Operation = "Merge", Inputs = flattened, OutputPath = finalStage,
                        Direction = set.Direction, MarginMm = set.MarginMm, LayerStyles = RevitLayerMappingService.GetAppearances(layers) },
                        flattened[0], setFolder, cancel, pump);
                    CheckCancel(cancel);
                    string destination = PublishUnique(finalStage, outputFolder, set.Name);
                    item.Success = true; item.Message = destination; item.Placements = merged.Placements;
                    item.Warnings.AddRange(merged.Warnings);
                    item.ModelEntityCount = merged.ModelEntityCount; item.PaperEntityCount = merged.PaperEntityCount;
                }
                catch (OperationCanceledException) { item.Message = "사용자 취소 · 이 세트의 최종 파일은 생성하지 않았습니다."; result.Cancelled = true; break; }
                catch (Exception ex) { item.Message = ex.Message; }
            }
        }
        finally
        {
            result.ManifestPath = Path.Combine(outputFolder, $"ChangExport_Manifest_{jobId}.json");
            File.WriteAllText(result.ManifestPath, JsonSerializer.Serialize(new
            {
                jobId, executedAt = DateTimeOffset.Now, modelPath = document.PathName, revitVersion = document.Application.VersionNumber,
                addinVersion = ProductInfo.Version, exportSetup = setupName, dwgFormat = options.FileVersion.ToString(), outputSpace = "ModelSpace", units = "Sheet paper millimeters",
                postProcessor = ManagedDwgProcessor.EngineName, externalSoftwareRequired = false, mergedViewsForStaging = options.MergedViews, originalSetupModified = false, customFiltersApplied = false,
                requestedSets = sets, layerEdits = layers.Where(l => l.HasChanges).ToList(), result.Cancelled, result.WorkFolder, items = result.Items
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return result;
    }
    private static void CheckCancel(Func<bool> cancel) { if (cancel()) throw new OperationCanceledException(); }
    public static string PublishUnique(string source, string outputFolder, string name)
    {
        string clean = name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) clean = clean.Replace(invalid, '_');
        clean = clean.TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "세트";
        for (int index = 1; index < 100000; index++)
        {
            string target = Path.Combine(outputFolder, clean + (index == 1 ? "" : $"_v{index}") + ".dwg");
            try { File.Move(source, target, false); return target; }
            catch (IOException) when (File.Exists(target)) { }
        }
        throw new IOException("중복되지 않는 출력 파일명을 만들 수 없습니다.");
    }
}

public sealed class ExportRunResult
{
    public string OutputFolder { get; init; } = string.Empty;
    public string WorkFolder { get; init; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public bool Cancelled { get; set; }
    public List<ExportItemResult> Items { get; } = new();
    public int SuccessCount => Items.Count(x => x.Success);
    public int FailedCount => Items.Count(x => !x.Success);
}

public sealed class ExportItemResult
{
    public ExportItemResult(string sheetNumber, string sheetName, bool success, string message)
    { SheetNumber = sheetNumber; SheetName = sheetName; Success = success; Message = message; }
    public string SheetNumber { get; }
    public string SheetName { get; }
    public bool Success { get; set; }
    public string Message { get; set; }
    public List<string> Warnings { get; } = new();
    public List<SheetPlacement> Placements { get; set; } = new();
    public int ModelEntityCount { get; set; }
    public int PaperEntityCount { get; set; }
}
