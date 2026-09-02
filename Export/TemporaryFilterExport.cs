using Autodesk.Revit.DB;
using ChangExport.DwgProcessing;
using ChangExport.Models;
using Color = Autodesk.Revit.DB.Color;
using View = Autodesk.Revit.DB.View;

namespace ChangExport.Export;

/// <summary>Applies export-only overrides to independent copies. No source view is edited.</summary>
internal static class TemporaryFilterExport
{
    internal sealed class Result
    {
        public string Drawing { get; set; } = "";
        public List<ColorLayerRemap> Remaps { get; } = new();
        public List<MaterialAppearanceRemap> MaterialAppearanceRemaps { get; } = new();
        public Dictionary<string, string> TextReplacements { get; } = new();
        public Dictionary<string, int> MatchedElements { get; } = new();
    }

    public static Result Export(Document document, ViewSheet source, DWGExportOptions options,
        IReadOnlyList<RevitLayerRow> rows, IReadOnlyList<MaterialLayerRule> materialRules,
        string baselineDirectory, string directory, List<string> warnings, Func<bool> cancel)
    {
        var result = new Result();
        if (options.PropOverrides == PropOverrideMode.ByLayer)
            throw new InvalidOperationException("출력 옵션이 객체 재지정을 제외하여 필터를 반영할 수 없습니다. 기본 도면 표현은 유지했습니다.");
        var rules = rows.Where(r => r.IsCustom).ToList();
        var ruleIndex = new TypeRuleIndex(rules);
        var typeNames = new Dictionary<ElementId, string>();
        var materialIndex = new Dictionary<ElementId, MaterialLayerRule>();
        foreach (var rule in materialRules)
        {
            if (string.IsNullOrWhiteSpace(rule.MaterialUniqueId))
            {
                warnings.Add($"재료 필터 미적용: '{rule.MaterialName}' 재료가 현재 프로젝트에 없어 템플릿의 레이어·색상만 보존되어 있습니다.");
                continue;
            }
            if (document.GetElement(rule.MaterialUniqueId) is not Material material)
            { warnings.Add($"재료 필터 제외: '{rule.MaterialName}' 재료가 현재 프로젝트에 없습니다."); continue; }
            materialIndex[material.Id] = rule;
        }
        var used = ManagedDwgProcessor.UsedColorIndices(Directory.GetFiles(baselineDirectory, "*.dwg"));
        used.UnionWith(rows.SelectMany(r => new[] { r.Color, r.CutColor }).Where(i => i is >= 1 and <= 255));
        used.UnionWith(materialRules.Select(r => r.Color).Where(i => i is >= 1 and <= 255));
        int Rgb(int index) { var c = new ACadSharp.Color((short)index); return (c.R << 16) | (c.G << 8) | c.B; }
        Color ToRevit(int index) { var c = new ACadSharp.Color((short)index); return new Color(c.R, c.G, c.B); }
        var usedRgb = used.Select(Rgb).ToHashSet();
        // ACI has duplicate RGB entries; use one representative and exact RGB matching.
        var available = Enumerable.Range(1, 255).GroupBy(Rgb).Where(g => !usedRgb.Contains(g.Key)).Select(g => g.First()).ToQueue();
        var markers = new Dictionary<string, (Color Projection, Color Cut)>();
        (Color Projection, Color Cut) Register(string key, string projectionLayer, int projectionColor,
            string cutLayer, int cutColor, string ruleId, bool fills, int priority)
        {
            if (markers.TryGetValue(key, out var existing)) return existing;
            if (available.Count < 2) throw new InvalidOperationException("기존 도면 색상과 충돌하지 않는 필터 식별색이 부족합니다. 원본 출력은 보존했습니다.");
            int projection = available.Dequeue(), cut = available.Dequeue();
            var pair = (ToRevit(projection), ToRevit(cut)); markers[key] = pair;
            result.Remaps.Add(new ColorLayerRemap { MarkerAci = projection, Layer = projectionLayer, Color = projectionColor,
                RuleId = ruleId, RemapFills = fills, BoundaryPriority = priority });
            result.Remaps.Add(new ColorLayerRemap { MarkerAci = cut, Layer = cutLayer, Color = cutColor,
                RuleId = ruleId, RemapFills = fills, BoundaryPriority = priority });
            result.MatchedElements.TryAdd(ruleId, 0);
            return pair;
        }
        foreach (var rule in rules) Register("type:" + rule.RuleId, rule.Layer, rule.Color, rule.CutLayer, rule.CutColor, rule.RuleId, false, 0);
        foreach (var rule in materialRules) result.MatchedElements.TryAdd(rule.RuleId, 0);
        result.MaterialAppearanceRemaps.AddRange(LinkedModelFilterSupport.BuildMaterialRemaps(
            document, materialRules, result.MatchedElements, warnings));
        static int FunctionPriority(int value) => (MaterialFunctionAssignment)value switch
        {
            MaterialFunctionAssignment.Finish1 => 700,
            MaterialFunctionAssignment.Finish2 => 600,
            MaterialFunctionAssignment.Structure or MaterialFunctionAssignment.StructuralDeck => 500,
            MaterialFunctionAssignment.Substrate => 400,
            MaterialFunctionAssignment.Insulation => 300,
            MaterialFunctionAssignment.Membrane => 200,
            _ => 100
        };
        IReadOnlyCollection<ElementId> CompoundMaterials(Element element)
        {
            if (document.GetElement(element.GetTypeId()) is not HostObjAttributes type
                || type.GetCompoundStructure() is not { LayerCount: >= 2 } structure) return Array.Empty<ElementId>();
            return structure.GetLayers().Select(layer => layer.MaterialId)
                .Where(id => id != ElementId.InvalidElementId).Distinct().ToList();
        }
        RevitLayerRow? TypeRule(Element element)
        {
            if (element.Category == null || !ruleIndex.HasCategory(element.Category.Name)) return null;
            ElementId typeId = element.GetTypeId();
            if (!typeNames.TryGetValue(typeId, out string? typeName)) typeNames[typeId] = typeName = document.GetElement(typeId)?.Name ?? "";
            return ruleIndex.Match(element.Category.Name, typeName);
        }
        bool IsLayeredWallOrFloor(Element element)
        {
            if (element.Category?.Id.Value is not ((long)BuiltInCategory.OST_Walls or (long)BuiltInCategory.OST_Floors)) return false;
            return document.GetElement(element.GetTypeId()) is HostObjAttributes type
                && type.GetCompoundStructure() is { LayerCount: >= 2 };
        }
        static ElementId PartSource(Part part)
        {
            var sourceId = part.GetSourceElementIds().FirstOrDefault();
            return sourceId?.HostElementId ?? ElementId.InvalidElementId;
        }
        bool HasVisibleWrapping(Element element)
        {
            if (element is not Wall wall || document.GetElement(element.GetTypeId()) is not HostObjAttributes type
                || type.GetCompoundStructure() is not { LayerCount: >= 2 } structure
                || !Enumerable.Range(0, structure.LayerCount).Any(structure.ParticipatesInWrapping)) return false;
            try
            {
                return wall.GetValidWrappingLocationIndices().Any(wall.IsWrappingAtLocationAllowed);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { return false; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return false; }
        }
        (Color Projection, Color Cut, string Match)? WrappingSupport(Element element)
        {
            if (document.GetElement(element.GetTypeId()) is not HostObjAttributes type
                || type.GetCompoundStructure() is not { LayerCount: >= 2 } structure) return null;
            var layers = structure.GetLayers();
            var selected = Enumerable.Range(0, layers.Count)
                .Where(structure.ParticipatesInWrapping)
                .Select(index => (Layer: layers[index], Rule: materialIndex.GetValueOrDefault(layers[index].MaterialId)))
                .Where(item => item.Rule != null)
                .OrderByDescending(item => FunctionPriority((int)item.Layer.Function))
                .FirstOrDefault();
            if (selected.Rule is { } material)
            {
                string match = "wrap:" + material.RuleId;
                var marker = Register(match, material.Layer, material.Color, material.Layer, material.Color,
                    match, false, 1);
                return (marker.Projection, marker.Cut, match);
            }
            RevitLayerRow? row = TypeRule(element) ?? rows.FirstOrDefault(candidate => !candidate.IsCustom
                && candidate.CategoryId == element.Category?.Id.Value && candidate.SubcategoryId == null && candidate.SpecialType == -1)
                ?? rows.FirstOrDefault(candidate => !candidate.IsCustom && candidate.Category == element.Category?.Name
                    && candidate.Subcategory.Length == 0 && candidate.SpecialType == -1);
            if (row == null) return null;
            string fallback = "wrap-category:" + (row.CategoryId?.ToString() ?? row.Category);
            var categoryMarker = Register(fallback, row.Layer, row.Color, row.CutLayer, row.CutColor,
                fallback, false, 1);
            return (categoryMarker.Projection, categoryMarker.Cut, fallback);
        }
        using var group = new TransactionGroup(document, "창Export 임시 필터 출력 (복구)");
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("임시 출력 트랜잭션을 시작할 수 없습니다.");
        var temporaryIds = new List<ElementId>();
        try
        {
            ElementId sheetId;
            using (var transaction = new Transaction(document, "창Export 복제 시트 필터"))
            {
                transaction.Start();
                if (!source.CanBeDuplicated(SheetDuplicateOption.DuplicateSheetWithViewsAndDetailing))
                    throw new InvalidOperationException("상세를 포함한 독립 시트 복제가 불가능합니다.");
                sheetId = source.Duplicate(SheetDuplicateOption.DuplicateSheetWithViewsAndDetailing);
                var sheet = (ViewSheet)document.GetElement(sheetId); temporaryIds.Add(sheet.Id);
                string token = "CE_TMP_" + Guid.NewGuid().ToString("N");
                sheet.SheetNumber = token; sheet.Name = source.Name;
                var scheduled = sheet.get_Parameter(BuiltInParameter.SHEET_SCHEDULED);
                if (scheduled is { IsReadOnly: false }) scheduled.Set(0);
                result.TextReplacements[token] = source.SheetNumber;
                document.Regenerate();
                var originals = source.GetAllPlacedViews().ToHashSet();
                var views = sheet.GetAllPlacedViews().Select(id => document.GetElement(id)).OfType<View>().ToList();
                var sourcePorts = source.GetAllViewports().Select(id => (Viewport)document.GetElement(id)).ToList();
                foreach (Viewport port in sheet.GetAllViewports().Select(id => (Viewport)document.GetElement(id)))
                {
                    var original = sourcePorts.FirstOrDefault(p => p.GetBoxCenter().DistanceTo(port.GetBoxCenter()) < 1e-6);
                    if (original == null || originals.Contains(port.ViewId)) continue;
                    var originalView = (View)document.GetElement(original.ViewId); var copy = (View)document.GetElement(port.ViewId);
                    var title = copy.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                    string name = originalView.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString() ?? "";
                    if (title is { IsReadOnly: false }) title.Set(string.IsNullOrEmpty(name) ? originalView.Name : name);
                }
                views.Add(sheet);
                var usableViews = new List<View>();
                foreach (View view in views)
                {
                    if (cancel()) throw new OperationCanceledException();
                    if (originals.Contains(view.Id))
                    {
                        warnings.Add($"필터 제외: 공유 뷰 '{view.Name}'는 독립 복제되지 않아 원본 표시를 유지했습니다."); continue;
                    }
                    temporaryIds.Add(view.Id);
                    if (!view.AreGraphicsOverridesAllowed()) { warnings.Add($"필터 제외: '{view.Name}'는 그래픽 재지정을 지원하지 않습니다."); continue; }
                    // Detach only a temporary copy; the source template and view remain untouched.
                    if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
                    document.Regenerate();
                    usableViews.Add(view);
                }
                LinkedModelFilterSupport.ApplyTypeFilters(document, usableViews, rules,
                    rule => markers["type:" + rule.RuleId], temporaryIds, result.MatchedElements, warnings);
                var partSources = new HashSet<ElementId>();
                var viewsWithMaterialParts = new HashSet<ElementId>();
                var sourcesByView = new Dictionary<ElementId, HashSet<ElementId>>();
                var wrappingByView = new Dictionary<ElementId, HashSet<ElementId>>();
                if (materialIndex.Count > 0)
                foreach (View view in usableViews)
                foreach (Element element in new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType().ToElements())
                {
                    if (element is ImportInstance or Part || !IsLayeredWallOrFloor(element)) continue;
                    var ids = CompoundMaterials(element);
                    if (!ids.Any(materialIndex.ContainsKey)) continue;
                    sourcesByView.TryAdd(view.Id, new()); sourcesByView[view.Id].Add(element.Id);
                    if (HasVisibleWrapping(element))
                    { wrappingByView.TryAdd(view.Id, new()); wrappingByView[view.Id].Add(element.Id); }
                    if (PartUtils.HasAssociatedParts(document, element.Id))
                    {
                        viewsWithMaterialParts.Add(view.Id);
                        continue;
                    }
                    try
                    {
                        if (PartUtils.AreElementsValidForCreateParts(document, new[] { element.Id }))
                        {
                            partSources.Add(element.Id);
                            viewsWithMaterialParts.Add(view.Id);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                }
                if (partSources.Count > 0)
                {
                    PartUtils.CreateParts(document, partSources);
                    document.Regenerate();
                    foreach (ElementId sourceId in partSources)
                        temporaryIds.AddRange(PartUtils.GetAssociatedParts(document, sourceId, false, true));
                }
                foreach (View view in usableViews.Where(v => v is not ViewSheet && viewsWithMaterialParts.Contains(v.Id)))
                {
                    var wrapped = wrappingByView.GetValueOrDefault(view.Id);
                    if (wrapped is not { Count: > 0 }) { view.PartsVisibility = PartsVisibility.ShowPartsOnly; continue; }
                    view.PartsVisibility = PartsVisibility.ShowPartsAndOriginal;
                    var hidden = sourcesByView.GetValueOrDefault(view.Id)?.Where(id => !wrapped.Contains(id)
                        && document.GetElement(id) is { } source && !source.IsHidden(view) && source.CanBeHidden(view)).ToList() ?? new();
                    if (hidden.Count > 0) view.HideElements(hidden);
                }
                document.Regenerate();
                foreach (View view in usableViews)
                foreach (Element element in new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType().ToElements())
                {
                    if (element is ImportInstance || element.Category == null) continue;
                    MaterialLayerRule? materialRule = null; int priority = 100; Element sourceElement = element;
                    bool suppressHostFills = false;
                    (Color Projection, Color Cut, string Match)? support = null;
                    if (element is not Part && wrappingByView.GetValueOrDefault(view.Id)?.Contains(element.Id) == true)
                    {
                        support = WrappingSupport(element);
                        suppressHostFills = support != null;
                    }
                    if (element is Part part)
                    {
                        ElementId sourceId = PartSource(part);
                        if (sourceId != ElementId.InvalidElementId && document.GetElement(sourceId) is { } original) sourceElement = original;
                        if (IsLayeredWallOrFloor(sourceElement))
                        {
                            ElementId materialId = part.get_Parameter(BuiltInParameter.DPART_MATERIAL_ID_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                            materialIndex.TryGetValue(materialId, out materialRule);
                            priority = FunctionPriority(part.get_Parameter(BuiltInParameter.DPART_LAYER_FUNCTION)?.AsInteger() ?? 0);
                        }
                    }
                    (Color Projection, Color Cut) marker;
                    string matchedRule;
                    bool countMatch = true;
                    if (support is { } host)
                    {
                        marker = (host.Projection, host.Cut); matchedRule = host.Match; countMatch = false;
                    }
                    else if (materialRule != null)
                    {
                        marker = Register($"material:{materialRule.RuleId}:{priority}", materialRule.Layer, materialRule.Color,
                            materialRule.Layer, materialRule.Color, materialRule.RuleId, true, priority);
                        matchedRule = materialRule.RuleId;
                    }
                    else if (TypeRule(sourceElement) is { } typeRule)
                    {
                        marker = markers["type:" + typeRule.RuleId]; matchedRule = typeRule.RuleId;
                    }
                    else continue;
                    using var settings = view.GetElementOverrides(element.Id);
                    // Only line colors carry private filter markers. Fill appearance stays
                    // exactly as displayed by Revit and the managed stage changes its layer only.
                    settings.SetProjectionLineColor(marker.Projection).SetCutLineColor(marker.Cut);
                    if (suppressHostFills)
                        settings.SetSurfaceForegroundPatternVisible(false).SetSurfaceBackgroundPatternVisible(false)
                            .SetCutForegroundPatternVisible(false).SetCutBackgroundPatternVisible(false);
                    view.SetElementOverrides(element.Id, settings);
                    if (countMatch) result.MatchedElements[matchedRule]++;
                }
                int wrappingHosts = wrappingByView.Values.SelectMany(ids => ids).Distinct().Count();
                if (wrappingHosts > 0)
                    warnings.Add($"끝단 마감 보존: 복합 벽 {wrappingHosts:N0}개는 원본 돌림마감 선형을 재료 Part와 함께 출력하고 중복 경계는 Part를 우선합니다.");
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("임시 필터 적용에 실패했습니다.");
            }
            if (result.MatchedElements.Values.Sum() == 0)
            {
                result.Drawing = Path.Combine(baselineDirectory, "sheet.dwg"); result.Remaps.Clear(); result.TextReplacements.Clear();
                warnings.Add("필터: 호스트와 로드된 링크에서 일치하는 재료·유형이 없어 기본 카테고리 DWG를 유지했습니다.");
                return result;
            }
            if (cancel()) throw new OperationCanceledException();
            Directory.CreateDirectory(directory);
            using var filteredOptions = new DWGExportOptions(options);
            // Keep the selected override/color behavior for unmatched objects.
            if (!document.Export(directory, "sheet", new List<ElementId> { sheetId }, filteredOptions))
                throw new IOException("필터 복제 시트의 DWG 생성에 실패했습니다.");
            result.Drawing = Path.Combine(directory, "sheet.dwg");
            if (!File.Exists(result.Drawing)) throw new IOException("필터 시트 DWG가 없습니다.");
            foreach (var rule in rules)
                warnings.Add($"필터 판정: {rule.Category} / 유형 이름 포함 '{rule.TypeNameContains}' · Revit 객체 {result.MatchedElements[rule.RuleId]:N0}개 (최종 DWG 반영 개수는 별도 기록)");
            foreach (var rule in materialRules)
                warnings.Add($"재료 필터 판정: '{rule.MaterialName}' → {rule.Layer} · Revit 객체/Part {result.MatchedElements.GetValueOrDefault(rule.RuleId):N0}개 (하부 표현 점선 제외, 최종 DWG 반영 개수는 별도 기록)");
        }
        finally
        {
            try
            {
                if (group.GetStatus() == TransactionStatus.Started && group.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("트랜잭션 그룹 복구 상태를 확인할 수 없습니다.");
                if (temporaryIds.Any(id => document.GetElement(id) != null))
                    throw new InvalidOperationException("임시 시트/뷰 잔존이 확인되었습니다.");
            }
            catch (Exception ex) { throw new TemporaryExportRestoreException("임시 출력 요소 복구 확인에 실패했습니다. 출력을 중단합니다.", ex); }
        }
        return result;
    }

    private static Queue<T> ToQueue<T>(this IEnumerable<T> source) => new(source);
}

internal sealed class TemporaryExportRestoreException(string message, Exception inner) : InvalidOperationException(message, inner);
