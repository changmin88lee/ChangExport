using Autodesk.Revit.DB;
using ChangExport.DwgProcessing;
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
                    using RevitLinkGraphicsSettings settings = view.GetLinkOverrides(link.Id);
                    if (settings.LinkVisibilityType != LinkVisibility.ByHostView)
                        settings.LinkVisibilityType = LinkVisibility.Custom;
                    settings.ViewFilterType = LinkVisibility.ByHostView;
                    view.SetLinkOverrides(link.Id, settings);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException
                    or Autodesk.Revit.Exceptions.InvalidOperationException)
                {
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
        warnings.Add($"링크 유형 필터: 로드된 링크 문서 {linkedDocuments.Count:N0}개에 호스트 유형 이름 규칙 {filters.Count:N0}개를 임시 전파했습니다.");
    }

    internal static List<MaterialAppearanceRemap> BuildMaterialRemaps(Document host,
        IReadOnlyList<MaterialLayerRule> rules, IDictionary<string, int> matches, List<string> warnings)
    {
        if (rules.Count == 0) return new();
        var linked = new HashSet<Document>();
        foreach (RevitLinkInstance instance in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            if (instance.GetLinkDocument() is { } document) CollectDocuments(document, linked);
        if (linked.Count == 0) return new();
        var documents = new[] { host }.Concat(linked).ToList();
        var appearances = new List<MaterialAppearance>();
        foreach (Document document in documents)
        {
            var priorities = MaterialPriorities(document);
            foreach (Material material in new FilteredElementCollector(document).OfClass(typeof(Material)).Cast<Material>())
            {
                int priority = priorities.GetValueOrDefault(material.Id, 100);
                AddAppearance(document, material, material.CutForegroundPatternId, material.CutForegroundPatternColor, priority, linked.Contains(document), appearances);
                AddAppearance(document, material, material.CutBackgroundPatternId, material.CutBackgroundPatternColor, priority, linked.Contains(document), appearances);
                AddAppearance(document, material, material.SurfaceForegroundPatternId, material.SurfaceForegroundPatternColor, priority, linked.Contains(document), appearances);
                AddAppearance(document, material, material.SurfaceBackgroundPatternId, material.SurfaceBackgroundPatternColor, priority, linked.Contains(document), appearances);
            }
        }
        var ruleByName = rules.GroupBy(rule => rule.MaterialName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<MaterialAppearanceRemap>();
        var colorOwners = appearances.GroupBy(item => (item.IsSolid, item.DisplayRgb))
            .ToDictionary(group => group.Key, group => group.Select(item => item.MaterialName)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        int ambiguous = 0;
        foreach (var group in appearances.GroupBy(item => item.Signature))
        {
            var linkedTargets = group.Where(item => item.IsLinked && ruleByName.ContainsKey(item.MaterialName)).ToList();
            if (linkedTargets.Count == 0) continue;
            var targetNames = linkedTargets.Select(item => item.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool collision = targetNames.Count != 1 || group.Select(item => item.MaterialName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Any(name => !string.Equals(name, targetNames[0], StringComparison.OrdinalIgnoreCase));
            if (collision) { ambiguous++; continue; }
            MaterialLayerRule rule = ruleByName[targetNames[0]];
            MaterialAppearance sample = linkedTargets[0];
            result.Add(new MaterialAppearanceRemap
            {
                Pattern = sample.Pattern,
                IsSolid = sample.IsSolid,
                DisplayRgb = sample.DisplayRgb,
                Layer = rule.Layer,
                Color = rule.Color,
                RuleId = rule.RuleId,
                MaterialName = rule.MaterialName,
                BoundaryPriority = linkedTargets.Max(item => item.Priority),
                AllowColorOnly = colorOwners[(sample.IsSolid, sample.DisplayRgb)].Count == 1,
                PatternLines = sample.PatternLines.Select(line => new MaterialPatternLine
                {
                    SpacingMm = line.SpacingMm,
                    ShiftMm = line.ShiftMm,
                    SegmentsMm = line.SegmentsMm.ToList()
                }).ToList()
            });
            matches.TryGetValue(rule.RuleId, out int current);
            matches[rule.RuleId] = Math.Max(1, current);
        }
        if (ambiguous > 0)
            warnings.Add($"링크 재료 충돌 보호: 서로 다른 재료가 같은 해치·색상을 쓰는 표시 서명 {ambiguous:N0}개는 오분류 방지를 위해 자동 적용하지 않습니다.");
        foreach (MaterialLayerRule rule in rules.Where(rule => linked.SelectMany(document =>
                     new FilteredElementCollector(document).OfClass(typeof(Material)).Cast<Material>())
                .Any(material => string.Equals(material.Name, rule.MaterialName, StringComparison.OrdinalIgnoreCase))))
        {
            int count = result.Count(map => map.RuleId == rule.RuleId);
            if (count == 0) warnings.Add($"링크 재료 필터 확인 필요: '{rule.MaterialName}'은 링크에 있지만 고유한 해치·색상 서명이 없어 경계를 안전하게 구분할 수 없습니다.");
            else warnings.Add($"링크 재료 필터: '{rule.MaterialName}' → {rule.Layer} · 고유 표시 서명 {count:N0}개 준비");
        }
        return result;
    }

    private static void CollectDocuments(Document document, ISet<Document> documents)
    {
        if (!documents.Add(document)) return;
        foreach (RevitLinkInstance nested in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            if (nested.GetLinkDocument() is { } linked) CollectDocuments(linked, documents);
    }

    private static Dictionary<ElementId, int> MaterialPriorities(Document document)
    {
        var result = new Dictionary<ElementId, int>();
        foreach (HostObjAttributes type in new FilteredElementCollector(document).OfClass(typeof(HostObjAttributes)).Cast<HostObjAttributes>())
        {
            CompoundStructure? structure = type.GetCompoundStructure();
            if (structure == null) continue;
            foreach (CompoundStructureLayer layer in structure.GetLayers())
            {
                if (layer.MaterialId == ElementId.InvalidElementId) continue;
                int priority = FunctionPriority(layer.Function);
                result[layer.MaterialId] = Math.Max(priority, result.GetValueOrDefault(layer.MaterialId));
            }
        }
        return result;
    }

    private static void AddAppearance(Document document, Material material, ElementId patternId, Color color,
        int priority, bool linked, ICollection<MaterialAppearance> output)
    {
        if (patternId == ElementId.InvalidElementId || !color.IsValid || document.GetElement(patternId) is not FillPatternElement element) return;
        FillPattern pattern = element.GetFillPattern();
        string name = pattern.IsSolidFill ? "" : pattern.Name;
        int rgb = (color.Red << 16) | (color.Green << 8) | color.Blue;
        var lines = pattern.IsSolidFill ? new List<MaterialPatternLine>() : pattern.GetFillGrids().Select(grid => new MaterialPatternLine
        {
            SpacingMm = Math.Abs(grid.Offset) * 304.8,
            ShiftMm = Math.Abs(grid.Shift) * 304.8,
            SegmentsMm = grid.GetSegments().Select(segment => segment * 304.8).ToList()
        }).OrderBy(line => line.SpacingMm).ThenBy(line => line.ShiftMm)
            .ThenBy(line => string.Join(",", line.SegmentsMm.Select(value => Math.Round(value, 3)))).ToList();
        output.Add(new MaterialAppearance(material.Name, name, pattern.IsSolidFill, rgb, priority, linked, lines));
    }

    private static int FunctionPriority(MaterialFunctionAssignment function) => function switch
    {
        MaterialFunctionAssignment.Finish1 => 700,
        MaterialFunctionAssignment.Finish2 => 600,
        MaterialFunctionAssignment.Structure or MaterialFunctionAssignment.StructuralDeck => 500,
        MaterialFunctionAssignment.Substrate => 400,
        MaterialFunctionAssignment.Insulation => 300,
        MaterialFunctionAssignment.Membrane => 200,
        _ => 100
    };

    private sealed record MaterialAppearance(string MaterialName, string Pattern, bool IsSolid,
        int DisplayRgb, int Priority, bool IsLinked, List<MaterialPatternLine> PatternLines)
    {
        public string Signature => $"{IsSolid}\u001f{DisplayRgb}\u001f" + string.Join("|", PatternLines.Select(line =>
            $"{Math.Round(line.SpacingMm, 3)}:{Math.Round(line.ShiftMm, 3)}:{string.Join(',', line.SegmentsMm.Select(value => Math.Round(value, 3)))}"));
    }
}
