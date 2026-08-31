using Autodesk.Revit.DB;
using ChangExport.DwgProcessing;
using ChangExport.Models;

namespace ChangExport.Export;

internal static class ExportGeometryOptions
{
    // Output-only layer names distinguish line styles even when the user maps several
    // styles/categories to one final layer. Never rename a Revit style or element.
    internal static List<WideLineLayer> ConfigureWideLines(DWGExportOptions options, IReadOnlyList<RevitLayerRow> rows, string keyword)
    {
        var result = new List<WideLineLayer>();
        if (string.IsNullOrWhiteSpace(keyword)) return result;
        using var table = options.GetExportLayerTable();
        foreach (var row in rows.Where(r => !r.IsCustom && r.CategoryId == (long)BuiltInCategory.OST_Lines
            && r.Subcategory.Length > 0 && r.Subcategory.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            using var key = new ExportLayerKey(row.Category, row.Subcategory, (SpecialType)row.SpecialType);
            if (!table.ContainsKey(key)) continue;
            using var value = table[key];
            string token = "CE_WIDE_" + Guid.NewGuid().ToString("N");
            result.Add(new() { NativeLayer = token + "_P", TargetLayer = row.Layer, StyleName = row.Subcategory });
            result.Add(new() { NativeLayer = token + "_C", TargetLayer = row.CutLayer, StyleName = row.Subcategory });
            value.LayerName = token + "_P"; value.CutLayerName = token + "_C";
            table[key] = value;
        }
        options.SetExportLayerTable(table);
        return result;
    }

    internal static List<FamilyBlockSource> ReadFamilies(Document document, IEnumerable<string>? additionalFamilyIds = null)
    {
        var sources = new Dictionary<string, FamilyBlockSource>();
        var knownPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var selected = (additionalFamilyIds ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
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
                string familyIdentity = owner.ProjectInformation.UniqueId + ":" + symbol.Family.UniqueId;
                string exclusion = BlockExclusion(category.Id.Value, category.CategoryType == CategoryType.Model,
                    instance.ViewSpecific, symbol.Family.IsInPlace, selected.Contains(familyIdentity), out bool additional);
                string identity = owner.ProjectInformation.UniqueId + ":" + symbol.UniqueId;
                if (!sources.TryGetValue(identity, out var item))
                    sources[identity] = item = new FamilyBlockSource { Identity = identity, Label = symbol.Family.Name + " - " + symbol.Name,
                        IsTitleBlock = title, Category = category.Name, ExclusionReason = exclusion,
                        FamilyIdentity = familyIdentity, FamilyName = symbol.Family.Name, CanSelectAdditional = additional };
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
            foreach (RevitLinkInstance link in new FilteredElementCollector(owner).OfClass(typeof(RevitLinkInstance)))
                if (link.GetLinkDocument() is { } linked) Collect(linked);
        }
        Collect(document);
        return sources.Values.ToList();
    }

    internal static string BlockExclusion(long category, bool model, bool viewSpecific, bool inPlace, bool selected, out bool additional)
    {
        additional = false;
        if (inPlace) return "내부 작성 패밀리";
        bool title = category == (long)BuiltInCategory.OST_TitleBlocks;
        if (!title && (viewSpecific || !model)) return "독립 주석·2D 패밀리";
        if (category is (long)BuiltInCategory.OST_StructuralFraming or (long)BuiltInCategory.OST_StructuralFoundation
            or (long)BuiltInCategory.OST_Walls or (long)BuiltInCategory.OST_Floors) return "보·벽·바닥·기초";
        bool standard = title || category is (long)BuiltInCategory.OST_Doors or (long)BuiltInCategory.OST_Windows
            or (long)BuiltInCategory.OST_Columns or (long)BuiltInCategory.OST_StructuralColumns;
        additional = !standard;
        return standard || selected ? "" : "추가 패밀리 미선택";
    }

    private static string NativeName(string name)
    {
        foreach (char c in "<>/\\\":;?*|=,") name = name.Replace(c, '_');
        return name;
    }
}
