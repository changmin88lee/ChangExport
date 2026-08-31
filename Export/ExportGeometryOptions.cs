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

    internal static List<FamilyBlockSource> ReadFamilies(Document document)
    {
        var sources = new Dictionary<string, FamilyBlockSource>();
        var knownPrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (FamilyInstance instance in new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)))
        {
            var symbol = instance.Symbol;
            var category = instance.Category;
            bool title = category?.Id.Value == (long)BuiltInCategory.OST_TitleBlocks;
            if (symbol == null || category == null || (!title && (instance.ViewSpecific || category.CategoryType != CategoryType.Model
                || category.Id.Value is (long)BuiltInCategory.OST_Walls or (long)BuiltInCategory.OST_Floors))) continue;
            string identity = document.ProjectInformation.UniqueId + ":" + symbol.UniqueId;
            if (!sources.TryGetValue(identity, out var item))
                sources[identity] = item = new FamilyBlockSource { Identity = identity, Label = symbol.Family.Name + " - " + symbol.Name, IsTitleBlock = title };
            // Require the actual Revit family/type name AND a known symbol/instance ID.
            // Unknown or truncated native names fall back to editable primitives.
            foreach (long id in new[] { symbol.Id.Value, instance.Id.Value })
            {
                string prefix = NativeName(item.Label) + "-" + id + "-";
                if (knownPrefixes.Add(identity + "|" + prefix)) item.NativePrefixes.Add(prefix);
            }
        }
        return sources.Values.ToList();
    }

    private static string NativeName(string name)
    {
        foreach (char c in "<>/\\\":;?*|=,") name = name.Replace(c, '_');
        return name;
    }
}
