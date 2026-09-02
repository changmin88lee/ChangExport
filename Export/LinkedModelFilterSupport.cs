using Autodesk.Revit.DB;
using ChangExport.Models;
using Color = Autodesk.Revit.DB.Color;
using View = Autodesk.Revit.DB.View;

namespace ChangExport.Export;

/// <summary>Export-only support for loaded Revit links. No linked document is edited.</summary>
internal static class LinkedModelFilterSupport
{
    internal static void ApplyTypeFilters(Document host, IReadOnlyList<View> views,
        IReadOnlyList<RevitLayerRow> rules, Func<RevitLayerRow, (Color Projection, Color Cut)> marker,
        ICollection<ElementId> temporaryIds, IDictionary<string, int> matches, List<string> warnings)
    {
        if (rules.Count == 0 || views.Count == 0) return;
        var filters = new List<(RevitLayerRow Rule, ElementId Id)>();
        foreach (RevitLayerRow rule in rules)
        {
            if (rule.CategoryId is not long categoryValue || categoryValue >= 0 || string.IsNullOrWhiteSpace(rule.TypeNameContains))
            {
                warnings.Add($"링크 유형 필터 제외: '{rule.Category} / {rule.TypeNameContains}'의 Revit 카테고리를 확인할 수 없습니다.");
                continue;
            }
            try
            {
                var categoryId = new ElementId(categoryValue);
                var common = ParameterFilterUtilities.GetFilterableParametersInCommon(host, new[] { categoryId });
                ElementId? typeNameId = new[]
                    {
                        new ElementId(BuiltInParameter.ALL_MODEL_TYPE_NAME),
                        new ElementId(BuiltInParameter.SYMBOL_NAME_PARAM)
                    }
                    .FirstOrDefault(common.Contains);
                if (typeNameId == null)
                {
                    warnings.Add($"링크 유형 필터 제외: '{rule.Category}' 카테고리는 Revit 네이티브 '유형 이름' 필터를 지원하지 않습니다.");
                    continue;
                }
                var clauses = new List<ElementFilter>
                {
                    new ElementParameterFilter(ParameterFilterRuleFactory.CreateContainsRule(typeNameId, rule.TypeNameContains))
                };
                // Preserve ChangExport's first-match rule even when Revit combines
                // several view filters on the same linked element.
                foreach (RevitLayerRow earlier in rules.TakeWhile(candidate => !ReferenceEquals(candidate, rule))
                    .Where(candidate => candidate.CategoryId == rule.CategoryId && !string.IsNullOrWhiteSpace(candidate.TypeNameContains)))
                    clauses.Add(new ElementParameterFilter(
                        ParameterFilterRuleFactory.CreateContainsRule(typeNameId, earlier.TypeNameContains), true));
                ElementFilter elementFilter = clauses.Count == 1 ? clauses[0] : new LogicalAndFilter(clauses);
                string name = "CE_LINK_" + rule.RuleId + "_" + Guid.NewGuid().ToString("N")[..8];
                ParameterFilterElement filter = ParameterFilterElement.Create(host, name, new[] { categoryId }, elementFilter);
                temporaryIds.Add(filter.Id);
                filters.Add((rule, filter.Id));
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException
                or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                warnings.Add($"링크 유형 필터 제외: '{rule.Category} / {rule.TypeNameContains}' · {ex.Message}");
            }
        }
        if (filters.Count == 0) return;

        var linkedDocuments = new HashSet<Document>();
        int appliedLinkInstances = 0;
        int retainedLinkInstances = 0;
        foreach (View view in views.Where(view => view is not ViewSheet))
        {
            foreach (var item in filters)
            {
                var colors = marker(item.Rule);
                using var graphics = new OverrideGraphicSettings();
                graphics.SetProjectionLineColor(colors.Projection).SetCutLineColor(colors.Cut);
                view.SetFilterOverrides(item.Id, graphics);
                view.SetFilterVisibility(item.Id, true);
            }
            foreach (RevitLinkInstance link in new FilteredElementCollector(host, view.Id)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linked = link.GetLinkDocument();
                if (linked != null) CollectDocuments(linked, linkedDocuments);
                try
                {
                    using RevitLinkGraphicsSettings settings = view.GetLinkOverrides(link.Id) ?? new RevitLinkGraphicsSettings();
                    if (settings.LinkVisibilityType != LinkVisibility.ByHostView)
                        settings.LinkVisibilityType = LinkVisibility.Custom;
                    settings.ViewFilterType = LinkVisibility.ByHostView;
                    view.SetLinkOverrides(link.Id, settings);
                    appliedLinkInstances++;
                }
                catch (Exception ex)
                {
                    retainedLinkInstances++;
                    warnings.Add($"링크 표시 설정 유지: '{link.Name}'은 호스트 유형 필터 전환을 지원하지 않아 기존 표시를 사용합니다. · {ex.Message}");
                }
            }
        }
        foreach (Document linked in linkedDocuments)
        {
            var typeNames = new Dictionary<ElementId, string>();
            foreach (Element element in new FilteredElementCollector(linked).WhereElementIsNotElementType())
            {
                if (element.Category == null) continue;
                ElementId typeId = element.GetTypeId();
                if (!typeNames.TryGetValue(typeId, out string? typeName))
                    typeNames[typeId] = typeName = linked.GetElement(typeId)?.Name ?? "";
                RevitLayerRow? match = rules.FirstOrDefault(rule => rule.CategoryId == element.Category.Id.Value
                    && !string.IsNullOrWhiteSpace(rule.TypeNameContains)
                    && typeName.Contains(rule.TypeNameContains, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    matches.TryGetValue(match.RuleId, out int current);
                    matches[match.RuleId] = current + 1;
                }
            }
        }
        warnings.Add($"링크 유형 필터: 로드된 링크 문서 {linkedDocuments.Count:N0}개 · 뷰별 링크 인스턴스 적용 {appliedLinkInstances:N0}개"
            + (retainedLinkInstances == 0 ? "" : $" · 기존 표시 유지 {retainedLinkInstances:N0}개")
            + $" · 호스트 유형 이름 규칙 {filters.Count:N0}개");
    }

    private static void CollectDocuments(Document document, ISet<Document> documents)
    {
        if (!documents.Add(document)) return;
        foreach (RevitLinkInstance nested in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            if (nested.GetLinkDocument() is { } linked) CollectDocuments(linked, documents);
    }

}
