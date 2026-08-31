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
        options.MergedViews = false; // Keep native view references for explicit in-process binding.
        options.FileVersion = ACADVersion.R2010; // Export-only override; never modify the project's saved setup.
        options.TargetUnit = ExportUnit.Millimeter;
        try
        {
            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                SheetSetDefinition set = sets[setIndex];
                var item = new ExportItemResult(set.Name, string.Empty, false, "미실행");
                result.Items.Add(item);
                string stage = "시트 확인";
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
                        stage = $"{sheet.SheetNumber}: Revit 기본 DWG 생성";
                        var placedViews = sheet.GetAllPlacedViews().Select(id => document.GetElement(id)).OfType<Autodesk.Revit.DB.View>()
                            .Select(v => new { id = v.Id.Value, name = v.Name, type = v.ViewType.ToString(), scale = v.Scale }).ToList();
                        var schedules = new FilteredElementCollector(document, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                            .Select(s => new { id = s.ScheduleId.Value, name = document.GetElement(s.ScheduleId)?.Name }).ToList();
                        item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, sheetUniqueId = sheet.UniqueId, placedViews, schedules,
                            outlineFeet = new { minU = sheet.Outline.Min.U, minV = sheet.Outline.Min.V, maxU = sheet.Outline.Max.U, maxV = sheet.Outline.Max.V } });
                        progress($"{setIndex + 1}/{sets.Count} 세트 · {set.Name}\n{sheetIndex + 1}/{set.SheetUniqueIds.Count} 시트 · {sheet.SheetNumber} · Revit DWG 생성");
                        bool success = document.Export(nativeDirectory, "sheet", new List<ElementId> { sheet.Id }, options);
                        if (!success || !File.Exists(Path.Combine(nativeDirectory, "sheet.dwg"))) throw new IOException("Revit이 시트 DWG를 생성하지 못했습니다.");
                        CheckCancel(cancel);
                        var request = new BridgeRequest { Operation = "Flatten", RevitSheet = true, LayerStyles = RevitLayerMappingService.GetAppearances(layers) };
                        string input = Path.Combine(nativeDirectory, "sheet.dwg");
                        if (layers.Any(r => r.IsCustom))
                        {
                            stage = $"{sheet.SheetNumber}: 임시 복제 시트 필터";
                            progress($"{set.Name} · {sheet.SheetNumber}\n독립 복제 뷰에서 유형 이름 필터 적용 중");
                            try
                            {
                                var filtered = TemporaryFilterExport.Export(document, sheet, options, layers, nativeDirectory,
                                    Path.Combine(setFolder, $"filtered_{sheetIndex + 1:000}"), item.Warnings, cancel);
                                input = filtered.Drawing; request.ColorRemaps = filtered.Remaps.ToList(); request.TextReplacements = new(filtered.TextReplacements);
                                request.ExpectedRuleMatches = new(filtered.MatchedElements);
                                item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, filterMatches = filtered.MatchedElements, filterRemaps = filtered.Remaps });
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (TemporaryExportRestoreException) { throw; }
                            catch (Exception ex)
                            {
                                // The temporary exporter always rolls back before returning or throwing.
                                // Preserve the usable unfiltered sheet and explicitly report non-application.
                                item.Warnings.Add($"필터 미반영: 시트 {sheet.SheetNumber} · {ex.Message} · 기본 카테고리 출력은 계속합니다.");
                                item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, filterError = ex.ToString() });
                            }
                        }
                        string flat = Path.Combine(setFolder, $"flat_{sheetIndex + 1:000}.dwg");
                        stage = $"{sheet.SheetNumber}: 참조 결합·모형공간 변환";
                        progress($"{set.Name} · {sheet.SheetNumber}\n내장 엔진 모형공간 변환 및 재열기 검사 중");
                        request.OutputPath = flat;
                        BridgeResponse conversion;
                        try { conversion = processor.Run(request, input, setFolder, cancel, pump); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (input != Path.Combine(nativeDirectory, "sheet.dwg"))
                        {
                            item.Warnings.Add($"필터 출력 실패: {ex.Message} · 기본 카테고리 DWG로 저장을 계속했습니다.");
                            item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, filteredDwgError = ex.ToString() });
                            request.ColorRemaps.Clear(); request.TextReplacements.Clear(); request.ExpectedRuleMatches.Clear();
                            input = Path.Combine(nativeDirectory, "sheet.dwg");
                            conversion = processor.Run(request, input, setFolder, cancel, pump);
                        }
                        item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, input, nativeFiles = Directory.GetFiles(nativeDirectory).Select(Path.GetFileName),
                            conversion.ConvertedViewports, conversion.CustomRuleEntityCounts, conversion.ModelScale,
                            conversion.ExplodedInserts, conversion.BoundaryBlocksRetained });
                        if (placedViews.Count > 0 && Directory.GetFiles(nativeDirectory, "*.dwg").Length == 1)
                            item.Warnings.Add($"도면 내용 확인: 시트 {sheet.SheetNumber}의 배치 뷰는 {placedViews.Count}개지만 별도 뷰 DWG가 없습니다. 도곽만 생성된 경우를 포함하여 원본 시트와 비교하세요. 파일 저장과 내용 완전성은 별도입니다.");
                        item.Warnings.AddRange(conversion.Warnings.Select(w => $"시트 {sheet.SheetNumber}: {w}")); flattened.Add(flat);
                    }
                    CheckCancel(cancel);
                    string finalStage = Path.Combine(setFolder, "merged.dwg");
                    stage = "세트 모형공간 배치";
                    progress($"{set.Name}\n{set.SheetUniqueIds.Count}장 {(set.Direction == "Vertical" ? "세로" : "가로")} 배치 · 최종 DWG 검사 중");
                    var merged = processor.Run(new BridgeRequest { Operation = "Merge", Inputs = flattened, OutputPath = finalStage,
                        Direction = set.Direction, MarginMm = set.MarginMm, RevitSheet = true, LayerStyles = RevitLayerMappingService.GetAppearances(layers) },
                        flattened[0], setFolder, cancel, pump);
                    CheckCancel(cancel);
                    string destination = PublishUnique(finalStage, outputFolder, set.Name);
                    item.Success = true; item.Message = destination; item.Placements = merged.Placements;
                    item.Warnings.AddRange(merged.Warnings);
                    item.ModelEntityCount = merged.ModelEntityCount; item.PaperEntityCount = merged.PaperEntityCount;
                }
                catch (OperationCanceledException) { item.Message = "사용자 취소 · 이 세트의 최종 파일은 생성하지 않았습니다."; result.Cancelled = true; break; }
                catch (TemporaryExportRestoreException ex) { item.Message = stage + " · " + ex.Message; item.ErrorDetails = ex.ToString(); throw; }
                catch (Exception ex) { item.Message = stage + " · " + ex.Message; item.ErrorDetails = ex.ToString(); }
            }
        }
        finally
        {
            result.ManifestPath = Path.Combine(outputFolder, $"ChangExport_Manifest_{jobId}.json");
            File.WriteAllText(result.ManifestPath, JsonSerializer.Serialize(new
            {
                jobId, executedAt = DateTimeOffset.Now, modelPath = document.PathName, revitVersion = document.Application.VersionNumber,
                addinVersion = ProductInfo.Version, exportSetup = setupName, dwgFormat = options.FileVersion.ToString(), outputSpace = "ModelSpace", units = "Model millimeters; sheet scaled by largest 2D viewport denominator",
                postProcessor = ManagedDwgProcessor.EngineName, externalSoftwareRequired = false, mergedViewsForStaging = options.MergedViews, originalSetupModified = false,
                customFiltersRequested = layers.Count(r => r.IsCustom), customFilterMethod = "Independent temporary sheet/view copies, type-name contains, color marker remap, transaction-group rollback",
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
    public string ErrorDetails { get; set; } = "";
    public List<object> SheetDiagnostics { get; } = new();
}
