using System.Text.Json;
using System.Diagnostics;
using Autodesk.Revit.DB;
using ChangExport.App;
using ChangExport.DwgProcessing;
using ChangExport.Models;
using ChangExport.Standards;

namespace ChangExport.Export;

public sealed class RevitDwgExportService
{
    public ExportRunResult Export(Document document, IReadOnlyList<SheetSetDefinition> sets, string outputFolder,
        RevitExportConfiguration configuration, Action<string> progress, Func<bool> cancel, Action pump, string wideLineKeyword = "##")
    {
        var processor = new ManagedDwgProcessor();

        Directory.CreateDirectory(outputFolder);
        string jobId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
        string staging = Path.Combine(outputFolder, "_ChangExport_Work", jobId);
        Directory.CreateDirectory(staging);
        var result = new ExportRunResult { OutputFolder = outputFolder, WorkFolder = staging };
        var mappingService = new RevitLayerMappingService(document);
        var runtimes = new Dictionary<string, TemplateRuntime>(StringComparer.Ordinal);
        try
        {
            foreach (string templateId in sets.Select(s => s.TemplateId).Distinct(StringComparer.Ordinal))
            {
                var template = RevitLayerMappingService.FindTemplate(configuration, templateId)
                    ?? throw new InvalidDataException("시트에 지정된 DWG 레이어 템플릿을 찾을 수 없습니다.");
                var layers = mappingService.Read(templateId, configuration);
                var materialRules = RevitLayerMappingService.ReadMaterialRules(templateId, configuration);
                var issues = RevitLayerMappingService.Validate(layers).Concat(RevitLayerMappingService.ValidateMaterialRules(materialRules))
                    .Concat(RevitLayerMappingService.ValidateCombined(layers, materialRules)).Distinct().ToList();
                if (issues.Count > 0) throw new InvalidDataException($"{template.SetupName}: " + string.Join(Environment.NewLine, issues.Take(12)));
                var options = mappingService.Apply(template.SetupName, layers);
                options.MergedViews = false;
                options.FileVersion = ACADVersion.R2010;
                options.TargetUnit = ExportUnit.Millimeter;
                runtimes[templateId] = new TemplateRuntime(template, layers, materialRules, options,
                    ExportGeometryOptions.ConfigureWideLines(document, options, layers, wideLineKeyword));
            }
        }
        catch
        {
            foreach (var runtime in runtimes.Values) runtime.Options.Dispose();
            throw;
        }
        var allLayers = runtimes.Values.SelectMany(r => r.Layers).ToList();
        var allMaterialRules = runtimes.Values.SelectMany(r => r.MaterialRules).ToList();
        var blockSources = ExportGeometryOptions.ReadBlockSources(document);
        var excludedLayers = RevitLayerMappingService.InternalExcludedLayers(allLayers);
        var sheetSources = SheetSetService.ReadSheetSources(document);
        var sourceSessions = new Dictionary<string, LinkedSheetDocumentSession>(StringComparer.Ordinal);
        var linkedRuntimes = new Dictionary<(string Source, string Template), SourceTemplateRuntime>();
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
                    if (string.IsNullOrWhiteSpace(set.TemplateId) || !runtimes.TryGetValue(set.TemplateId, out var runtime))
                        throw new InvalidDataException("DWG 레이어 템플릿이 지정되지 않았거나 삭제된 세트입니다.");
                    if (set.SheetUniqueIds.Any(id => configuration.SheetTemplateIds.GetValueOrDefault(id, string.Empty) != set.TemplateId))
                        throw new InvalidDataException("세트에 서로 다른 DWG 레이어 템플릿을 사용하는 시트가 포함되어 있습니다.");
                    string setFolder = Path.Combine(staging, $"set_{setIndex + 1:000}"); Directory.CreateDirectory(setFolder);
                    using var preparedQueue = new DwgPreparationQueue(cancel, pump);
                    for (int sheetIndex = 0; sheetIndex < set.SheetUniqueIds.Count; sheetIndex++)
                    {
                        CheckCancel(cancel);
                        string sheetKey = set.SheetUniqueIds[sheetIndex];
                        if (!sheetSources.TryGetValue(sheetKey, out SheetSetService.SheetSource? sheetSource))
                            throw new InvalidOperationException("현재 로드된 호스트/링크 모델에 없는 시트입니다.");
                        string sessionKey = sheetSource.Sheet.IsHost ? "host" : sheetSource.Sheet.SourceKey;
                        if (!sourceSessions.TryGetValue(sessionKey, out LinkedSheetDocumentSession? session))
                        {
                            session = LinkedSheetDocumentSession.Open(document, sheetSource.Document,
                                sheetSource.Sheet.SourceName, staging, item.Warnings);
                            sourceSessions.Add(sessionKey, session);
                        }
                        Document sourceDocument = session.Document;
                        ViewSheet sheet = sourceDocument.GetElement(sheetSource.Sheet.UniqueId) as ViewSheet
                            ?? throw new InvalidOperationException($"원본 모델 '{sheetSource.Sheet.SourceName}'에서 시트를 다시 찾을 수 없습니다.");
                        string sheetLabel = sheetSource.Sheet.DisplayNumber;
                        List<RevitLayerRow> layers;
                        List<MaterialLayerRule> materialRules;
                        DWGExportOptions options;
                        List<WideLineLayer> wideLines;
                        List<FamilyBlockSource> sheetBlockSources;
                        if (sourceDocument == document)
                        {
                            layers = runtime.Layers; materialRules = runtime.MaterialRules; options = runtime.Options;
                            wideLines = runtime.WideLines; sheetBlockSources = blockSources;
                        }
                        else
                        {
                            var sourceRuntimeKey = (sessionKey, set.TemplateId);
                            if (!linkedRuntimes.TryGetValue(sourceRuntimeKey, out SourceTemplateRuntime? sourceRuntime))
                            {
                                var sourceMapping = new RevitLayerMappingService(sourceDocument);
                                var sourceLayers = sourceMapping.Read(set.TemplateId, configuration);
                                var sourceMaterials = RevitLayerMappingService.RebindImportedMaterials(runtime.MaterialRules,
                                    RevitMaterialCatalog.Read(sourceDocument));
                                var sourceIssues = RevitLayerMappingService.Validate(sourceLayers)
                                    .Concat(RevitLayerMappingService.ValidateMaterialRules(sourceMaterials))
                                    .Concat(RevitLayerMappingService.ValidateCombined(sourceLayers, sourceMaterials)).Distinct().ToList();
                                if (sourceIssues.Count > 0)
                                    throw new InvalidDataException($"{sheetSource.Sheet.SourceName} / {runtime.Template.SetupName}: "
                                        + string.Join(Environment.NewLine, sourceIssues.Take(12)));
                                var sourceOptions = sourceMapping.Apply(runtime.Template.SetupName, sourceLayers);
                                sourceOptions.MergedViews = false; sourceOptions.FileVersion = ACADVersion.R2010;
                                sourceOptions.TargetUnit = ExportUnit.Millimeter;
                                sourceRuntime = new SourceTemplateRuntime(sourceLayers, sourceMaterials, sourceOptions,
                                    ExportGeometryOptions.ConfigureWideLines(sourceDocument, sourceOptions, sourceLayers, wideLineKeyword),
                                    ExportGeometryOptions.ReadBlockSources(sourceDocument));
                                linkedRuntimes.Add(sourceRuntimeKey, sourceRuntime);
                            }
                            layers = sourceRuntime.Layers; materialRules = sourceRuntime.MaterialRules; options = sourceRuntime.Options;
                            wideLines = sourceRuntime.WideLines; sheetBlockSources = sourceRuntime.BlockSources;
                        }
                        string nativeDirectory = Path.Combine(setFolder, $"native_{sheetIndex + 1:000}"); Directory.CreateDirectory(nativeDirectory);
                        stage = $"{sheetLabel}: Revit 기본 DWG 생성";
                        var placedViews = sheet.GetAllPlacedViews().Select(id => sourceDocument.GetElement(id)).OfType<Autodesk.Revit.DB.View>()
                            .Select(v => new { id = v.Id.Value, name = v.Name, type = v.ViewType.ToString(), scale = v.Scale }).ToList();
                        var schedules = new FilteredElementCollector(sourceDocument, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                            .Select(s => new { id = s.ScheduleId.Value, name = sourceDocument.GetElement(s.ScheduleId)?.Name }).ToList();
                        item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, sheetUniqueId = sheet.UniqueId,
                            sourceModel = sheetSource.Sheet.SourceName, sourceKey = sheetSource.Sheet.SourceKey,
                            sourceKind = sheetSource.Sheet.IsHost ? "Host" : "LinkedRvt", linkDepth = sheetSource.Sheet.LinkDepth,
                            templateId = runtime.Template.SetupId, templateName = runtime.Template.SetupName, placedViews, schedules,
                            outlineFeet = new { minU = sheet.Outline.Min.U, minV = sheet.Outline.Min.V, maxU = sheet.Outline.Max.U, maxV = sheet.Outline.Max.V } });
                        progress($"{setIndex + 1}/{sets.Count} 세트 · {set.Name}\n{sheetIndex + 1}/{set.SheetUniqueIds.Count} 시트 · {sheetLabel} · Revit DWG 생성");
                        var nativeClock = Stopwatch.StartNew();
                        bool success = sourceDocument.Export(nativeDirectory, "sheet", new List<ElementId> { sheet.Id }, options);
                        item.TimingsMs[$"{sheetLabel}:native"] = nativeClock.Elapsed.TotalMilliseconds;
                        if (!success || !File.Exists(Path.Combine(nativeDirectory, "sheet.dwg"))) throw new IOException("Revit이 시트 DWG를 생성하지 못했습니다.");
                        CheckCancel(cancel);
                        var request = new BridgeRequest { Operation = "Flatten", RevitSheet = true, UseLayerColors = true,
                            LayerStyles = RevitLayerMappingService.GetAppearances(layers), WideLineLayers = wideLines, FamilySources = sheetBlockSources,
                            ExcludedLayers = excludedLayers };
                        string input = Path.Combine(nativeDirectory, "sheet.dwg");
                        if (layers.Any(r => r.IsCustom) || materialRules.Count > 0)
                        {
                            stage = $"{sheetLabel}: 임시 복제 시트 필터";
                            progress($"{set.Name} · {sheetLabel}\n독립 복제 뷰에서 복합재료·유형 이름 필터 적용 중");
                            try
                            {
                                var filterClock = Stopwatch.StartNew();
                                var filtered = TemporaryFilterExport.Export(sourceDocument, sheet, options, layers, materialRules, nativeDirectory,
                                    Path.Combine(setFolder, $"filtered_{sheetIndex + 1:000}"), item.Warnings, cancel);
                                request.FilterReferencePath = filtered.Drawing;
                                request.ColorRemaps = filtered.Remaps.ToList();
                                request.TextReplacements = new(filtered.TextReplacements);
                                item.TimingsMs[$"{sheetLabel}:filter"] = filterClock.Elapsed.TotalMilliseconds;
                                foreach (var timing in filtered.TimingsMs)
                                    item.TimingsMs[$"{sheetLabel}:filter:{timing.Key}"] = timing.Value;
                                request.ExpectedRuleMatches = new(filtered.MatchedElements);
                                item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, filterMatches = filtered.MatchedElements,
                                    filterRemaps = filtered.Remaps, linkedMaterialPartSources = filtered.LinkedMaterialPartSources });
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (TemporaryExportRestoreException) { throw; }
                            catch (Exception ex)
                            {
                                // The temporary exporter always rolls back before returning or throwing.
                                // Preserve the usable unfiltered sheet and explicitly report non-application.
                                item.Warnings.Add($"필터 미반영: 시트 {sheetLabel} · {ex.Message} · 기본 카테고리 출력은 계속합니다.");
                                item.SheetDiagnostics.Add(new { sheet = sheet.SheetNumber, filterError = ex.ToString() });
                            }
                        }
                        stage = $"{sheetLabel}: 참조 결합·모형공간 변환";
                        progress($"{set.Name} · {sheetLabel}\n내장 엔진 모형공간 변환 중 · 최종 저장 시 재열기 검사");
                        preparedQueue.Enqueue(sheetLabel, request, input, Path.Combine(nativeDirectory, "sheet.dwg"));
                        if (placedViews.Count > 0 && Directory.GetFiles(nativeDirectory, "*.dwg").Length == 1)
                            item.Warnings.Add($"도면 내용 확인: 시트 {sheetLabel}의 배치 뷰는 {placedViews.Count}개지만 별도 뷰 DWG가 없습니다. 도곽만 생성된 경우를 포함하여 원본 시트와 비교하세요. 파일 저장과 내용 완전성은 별도입니다.");
                    }
                    var prepared = preparedQueue.Finish();
                    foreach (var entry in prepared)
                    {
                        var conversion = entry.Drawing.Response;
                        item.SheetDiagnostics.Add(new { sheet = entry.Sheet, input = entry.Drawing.Source, conversion.ConvertedViewports,
                            conversion.CustomRuleEntityCounts, conversion.ModelScale, conversion.ExplodedInserts, conversion.BoundaryBlocksRetained, conversion.NormalizedEntityColors,
                            conversion.PreservedFillColors, conversion.PreservedMaskingEntities, conversion.MaterialBoundaryDuplicatesRemoved,
                            conversion.LinkedMaterialFillsRemapped, conversion.LinkedMaterialBoundariesRemapped,
                            conversion.FilterContainerMarkersIgnored, conversion.FilterLowerGraphicsSkipped, conversion.ExcludedEntities,
                            conversion.GeometrySource, conversion.NativeOverlayMatchedEntities,
                            conversion.NativeOverlayUnmatchedMarkers, conversion.NativeOverlayAmbiguousMarkers,
                            conversion.WideLineConverted, conversion.WideLineSkipped, conversion.PreservedWideLineColors,
                            conversion.WideLineStyleCounts, conversion.FamilyBlockReferences, conversion.FamilyBlockDefinitions,
                            conversion.FamilySignaturesComputed, conversion.FamilySignaturesSkipped, conversion.FamilySignatureCacheHits,
                            conversion.FamilyBlockFallbacks, conversion.FamilyBlockMatches });
                        item.Warnings.AddRange(conversion.Warnings.Select(w => $"시트 {entry.Sheet}: {w}"));
                        foreach (var timing in conversion.TimingsMs) item.TimingsMs[$"{entry.Sheet}:{timing.Key}"] = timing.Value;
                    }
                    CheckCancel(cancel);
                    string finalStage = Path.Combine(setFolder, "merged.dwg");
                    stage = "세트 모형공간 배치";
                    progress($"{set.Name}\n{set.SheetUniqueIds.Count}장 {(set.Direction == "Vertical" ? "세로" : "가로")} 배치 · 최종 DWG 검사 중");
                    var merged = processor.MergePrepared(new BridgeRequest { Operation = "Merge", OutputPath = finalStage,
                        Direction = set.Direction, MarginMm = configuration.SheetSpacingMm, RevitSheet = true, UseLayerColors = true,
                        LayerStyles = RevitLayerMappingService.GetAppearances(runtime.Layers), WideLineLayers = runtime.WideLines,
                        ExcludedLayers = excludedLayers },
                        prepared.Select(p => p.Drawing).ToList(), setFolder, cancel, pump);
                    foreach (var timing in merged.TimingsMs) item.TimingsMs[timing.Key] = timing.Value;
                    CheckCancel(cancel);
                    string destination = PublishUnique(finalStage, outputFolder, set.Name);
                    item.Success = true; item.Message = destination; item.Placements = merged.Placements;
                    item.Warnings.AddRange(merged.Warnings);
                    item.SheetDiagnostics.Add(new { stage = "merged", merged.FamilyBlockReferences, merged.FamilyBlockDefinitions });
                    item.ModelEntityCount = merged.ModelEntityCount; item.PaperEntityCount = merged.PaperEntityCount;
                }
                catch (OperationCanceledException) { item.Message = "사용자 취소 · 이 세트의 최종 파일은 생성하지 않았습니다."; result.Cancelled = true; break; }
                catch (TemporaryExportRestoreException ex) { item.Message = stage + " · " + ex.Message; item.ErrorDetails = ex.ToString(); throw; }
                catch (Exception ex) { item.Message = stage + " · " + ex.Message; item.ErrorDetails = ex.ToString(); }
            }
        }
        finally
        {
            foreach (SourceTemplateRuntime runtime in linkedRuntimes.Values) runtime.Options.Dispose();
            foreach (LinkedSheetDocumentSession session in sourceSessions.Values.Reverse())
            {
                session.Dispose();
                if (!string.IsNullOrWhiteSpace(session.CleanupWarning))
                    foreach (ExportItemResult item in result.Items) item.Warnings.Add(session.CleanupWarning);
            }
            result.ManifestPath = Path.Combine(outputFolder, $"ChangExport_Manifest_{jobId}.json");
            File.WriteAllText(result.ManifestPath, JsonSerializer.Serialize(new
            {
                jobId, executedAt = DateTimeOffset.Now, modelPath = document.PathName, revitVersion = document.Application.VersionNumber,
                addinVersion = ProductInfo.Version, dwgFormat = ACADVersion.R2010.ToString(), outputSpace = "ModelSpace", units = "Model millimeters; sheet scaled by largest 2D viewport denominator",
                outputSetupSource = "ChangExport", templates = runtimes.Values.Select(r => new { id = r.Template.SetupId, name = r.Template.SetupName,
                    categoryRows = r.Layers.Count(row => !row.IsCustom), typeFilters = r.Layers.Count(row => row.IsCustom), materialFilters = r.MaterialRules.Count,
                    layerColors = RevitLayerMappingService.GetAppearances(r.Layers), wideLineLayers = r.WideLines }), intermediateDwgWritten = false,
                entityColorPolicy = "Revit TrueColor for hatch/solid fills; masking as WIPEOUT; other entities ByLayer after custom filter remapping",
                invisibleLinePolicy = "Revit <Invisible Lines> removed on private staging layer",
                wideLineKeyword, wideLineWidthSource = "Native Revit DWG lineweight in paper mm × sheet scale",
                wideLineColorPolicy = "Only successfully converted ## polylines use explicit Revit line-style RGB; black/white are swapped",
                familyBlockPolicy = "Automatic fixed-geometry loadable families and Revit detail groups; excludes in-place, path/sketch/two-level/adaptive families, structural framing/columns, curtain wall and railing system components",
                blockSources = blockSources.Select(f => new { f.Identity, f.Label, f.Category, f.SourceKind, f.PlacementType,
                    f.IsTitleBlock, f.IsDetailGroup, f.NativeLabels, f.NativeElementIds, f.ExclusionReason, knownPrefixCount = f.NativePrefixes.Count }),
                postProcessor = ManagedDwgProcessor.EngineName, externalSoftwareRequired = false, mergedViewsForStaging = false, originalSetupModified = false,
                sheetSourcePolicy = "Host and loaded local/network Revit links, including nested loaded links; linked RVTs are filtered in disposable local copies and never modified",
                customFiltersRequested = runtimes.Values.Sum(r => r.Layers.Count(row => row.IsCustom)), materialFiltersRequested = runtimes.Values.Sum(r => r.MaterialRules.Count),
                geometryEngine = "Native Geometry Engine (NGE)",
                geometrySourcePolicy = "The original Revit native sheet DWG is the only final geometry source; the temporary filtered DWG supplies classification evidence only",
                customFilterMethod = "Independent temporary sheet/view copies; host and linked compound wall/floor Parts; linked type-name filters propagated by host view filters; per-material unique color markers; exact native-geometry classification overlay; transaction-group rollback; lower/beyond graphics excluded",
                materialRules = allMaterialRules,
                sheetSpacingMm = configuration.SheetSpacingMm, requestedSets = sets,
                layerEdits = allLayers.Where(l => l.HasChanges).ToList(), result.Cancelled, result.WorkFolder, items = result.Items
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var runtime in runtimes.Values) runtime.Options.Dispose();
        }
        return result;
    }
    private sealed record TemplateRuntime(ExportSetupEdits Template, List<RevitLayerRow> Layers,
        List<MaterialLayerRule> MaterialRules, DWGExportOptions Options, List<WideLineLayer> WideLines);
    private sealed record SourceTemplateRuntime(List<RevitLayerRow> Layers, List<MaterialLayerRule> MaterialRules,
        DWGExportOptions Options, List<WideLineLayer> WideLines, List<FamilyBlockSource> BlockSources);
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
    public Dictionary<string, double> TimingsMs { get; } = new();
}
