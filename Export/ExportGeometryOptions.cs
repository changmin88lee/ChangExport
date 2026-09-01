using Autodesk.Revit.DB;
using ChangExport.DwgProcessing;
using ChangExport.Models;
using RevitColor = Autodesk.Revit.DB.Color;

namespace ChangExport.Export;

internal static class ExportGeometryOptions
{
    // Output-only layer names distinguish line styles even when the user maps several
    // styles/categories to one final layer. Never rename a Revit style or element.
    internal static List<WideLineLayer> ConfigureWideLines(Document document, DWGExportOptions options,
        IReadOnlyList<RevitLayerRow> rows, string keyword)
    {
        var result = new List<WideLineLayer>();
        if (string.IsNullOrWhiteSpace(keyword)) return result;
        using var table = options.GetExportLayerTable();
        foreach (var row in rows.Where(r => !r.IsCustom && r.CategoryId == (long)BuiltInCategory.OST_Lines
            && r.Subcategory.Length > 0 && r.Subcategory.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            Category? style = Category.GetCategory(document, new ElementId(row.SubcategoryId ?? 0));
            if (style == null || style.Parent?.Id.Value != (long)BuiltInCategory.OST_Lines)
                throw new InvalidDataException($"전역폭 선스타일의 Revit 색상을 읽을 수 없습니다: {row.Subcategory}");
            RevitColor revit = style.LineColor;
            int rgb = (revit.Red << 16) | (revit.Green << 8) | revit.Blue;
            // Revit black must display white in CAD and Revit white must display
            // black. Every chromatic color is kept byte-for-byte.
            int displayRgb = CadDisplayRgb(rgb);
            using var key = new ExportLayerKey(row.Category, row.Subcategory, (SpecialType)row.SpecialType);
            if (!table.ContainsKey(key)) continue;
            using var value = table[key];
            string token = "CE_WIDE_" + Guid.NewGuid().ToString("N");
            result.Add(new() { NativeLayer = token + "_P", TargetLayer = row.Layer, StyleName = row.Subcategory, DisplayRgb = displayRgb });
            result.Add(new() { NativeLayer = token + "_C", TargetLayer = row.CutLayer, StyleName = row.Subcategory, DisplayRgb = displayRgb });
            value.LayerName = token + "_P"; value.CutLayerName = token + "_C";
            table[key] = value;
        }
        options.SetExportLayerTable(table);
        return result;
    }

    internal static int CadDisplayRgb(int revitRgb) => revitRgb == 0 ? 0xFFFFFF : revitRgb == 0xFFFFFF ? 0 : revitRgb;

    internal static List<FamilyBlockSource> ReadBlockSources(Document document)
    {
        var sources = new Dictionary<string, FamilyBlockSource>();
        var knownPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Document>();
        void Collect(Document owner)
        {
            if (!visited.Add(owner)) return;
            foreach (FamilyInstance instance in new FilteredElementCollector(owner).OfClass(typeof(FamilyInstance)))
            {
                var symbol = instance.Symbol;
                var category = instance.Category;
                bool title = category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks;
                if (symbol == null || category == null) continue;
                string exclusion = BlockExclusion(category.Id.Value, category.CategoryType == CategoryType.Model,
                    instance.ViewSpecific, symbol.Family.IsInPlace, symbol.Family.FamilyPlacementType);
                string identity = owner.ProjectInformation.UniqueId + ":" + symbol.UniqueId;
                if (!sources.TryGetValue(identity, out var item))
                    sources[identity] = item = new FamilyBlockSource { Identity = identity, Label = symbol.Family.Name + " - " + symbol.Name,
                        IsTitleBlock = title, Category = category.Name, ExclusionReason = exclusion,
                        SourceKind = "LoadableFamily", PlacementType = symbol.Family.FamilyPlacementType.ToString() };
                // Collected Revit names permit full-name matching when native IDs represent
                // geometry variants or linked elements. Excluded types remain in the index
                // so an identical name cannot accidentally select a different allowed type.
                string label = NativeName(item.Label);
                if (!item.NativeLabels.Contains(label)) item.NativeLabels.Add(label);
                foreach (long id in new[] { symbol.Id.Value, instance.Id.Value })
                {
                    string prefix = label + "-" + id + "-";
                    if (knownPrefixes.Add(identity + "|" + prefix)) item.NativePrefixes.Add(prefix);
                }
            }
            foreach (Group group in new FilteredElementCollector(owner).OfClass(typeof(Group)))
            {
                var category = group.Category;
                if (category == null || category.Id.Value != (long)BuiltInCategory.OST_IOSDetailGroups
                    && category.Id.Value != (long)BuiltInCategory.OST_IOSAttachedDetailGroups) continue;
                GroupType type = group.GroupType;
                string identity = owner.ProjectInformation.UniqueId + ":detail-group:" + type.UniqueId;
                if (!sources.TryGetValue(identity, out var item))
                    sources[identity] = item = new FamilyBlockSource { Identity = identity, Label = type.Name,
                        Category = category.Name, SourceKind = "DetailGroup", IsDetailGroup = true, PlacementType = "DetailGroup" };
                string label = NativeName(type.Name);
                if (!item.NativeLabels.Contains(label)) item.NativeLabels.Add(label);
                foreach (long id in new[] { type.Id.Value, group.Id.Value })
                {
                    string value = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!item.NativeElementIds.Contains(value)) item.NativeElementIds.Add(value);
                }
            }
            foreach (RevitLinkInstance link in new FilteredElementCollector(owner).OfClass(typeof(RevitLinkInstance)))
                if (link.GetLinkDocument() is { } linked) Collect(linked);
        }
        Collect(document);
        return sources.Values.ToList();
    }

    internal static string BlockExclusion(long category, bool model, bool viewSpecific, bool inPlace, FamilyPlacementType placement)
    {
        if (inPlace) return "내부 작성 패밀리";
        bool title = category == (long)BuiltInCategory.OST_TitleBlocks;
        if (!title && (viewSpecific || !model)) return "독립 주석·2D 패밀리";
        if (title) return "";
        if (category is (long)BuiltInCategory.OST_StructuralFraming
            or (long)BuiltInCategory.OST_Columns or (long)BuiltInCategory.OST_StructuralColumns)
            return "인스턴스 길이·높이가 달라지는 구조 요소";
        if (category is (long)BuiltInCategory.OST_CurtainWallPanels or (long)BuiltInCategory.OST_CurtainWallMullions)
            return "커튼월 시스템 구성요소";
        if (category is (long)BuiltInCategory.OST_Railings or (long)BuiltInCategory.OST_RailingSystem
            or (long)BuiltInCategory.OST_RailingSupport or (long)BuiltInCategory.OST_RailingSystemBaluster
            or (long)BuiltInCategory.OST_RailingSystemHandRail or (long)BuiltInCategory.OST_RailingSystemHandRailBracket
            or (long)BuiltInCategory.OST_RailingSystemHardware or (long)BuiltInCategory.OST_RailingSystemPanel
            or (long)BuiltInCategory.OST_RailingSystemPost or (long)BuiltInCategory.OST_RailingSystemRail
            or (long)BuiltInCategory.OST_RailingSystemSegment or (long)BuiltInCategory.OST_RailingSystemTermination
            or (long)BuiltInCategory.OST_RailingSystemTopRail or (long)BuiltInCategory.OST_RailingSystemTransition
            or (long)BuiltInCategory.OST_RailingBalusterRail or (long)BuiltInCategory.OST_RailingHandRail
            or (long)BuiltInCategory.OST_RailingTopRail or (long)BuiltInCategory.OST_RailingTermination
            or (long)BuiltInCategory.OST_StairsRailing or (long)BuiltInCategory.OST_StairsRailingBaluster or (long)BuiltInCategory.OST_StairsRailingRail
            or (long)BuiltInCategory.OST_StairsSupports)
            return "난간·계단 시스템 구성요소";
        if (placement is FamilyPlacementType.TwoLevelsBased or FamilyPlacementType.CurveBased
            or FamilyPlacementType.CurveBasedDetail or FamilyPlacementType.CurveDrivenStructural
            or FamilyPlacementType.Adaptive or FamilyPlacementType.Invalid)
            return "스케치·경로·레벨·인스턴스 형상 가변 패밀리";
        return "";
    }

    private static string NativeName(string name)
    {
        foreach (char c in "<>/\\\":;?*|=,") name = name.Replace(c, '_');
        return name;
    }
}
