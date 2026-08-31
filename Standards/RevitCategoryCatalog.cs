using Autodesk.Revit.DB;
using ChangExport.Models;

namespace ChangExport.Standards;

public static class RevitCategoryCatalog
{
    public static List<RevitLayerRow> Read(Document document)
    {
        var rows = new Dictionary<string, RevitLayerRow>();
        var visited = new HashSet<long>();
        var imported = new FilteredElementCollector(document).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .Where(i => i.Category != null).Select(i => i.Category.Id.Value).ToHashSet();
        void Add(Category category)
        {
            if (!visited.Add(category.Id.Value)) return;
            var parent = category.Parent ?? category;
            if (imported.Contains(parent.Id.Value) || parent.Id.Value > 0) return;
            if (parent.CategoryType is not (CategoryType.Model or CategoryType.Annotation or CategoryType.AnalyticalModel)
                && !parent.IsVisibleInUI && parent.Id.Value != (long)BuiltInCategory.OST_Lines) return;
            string subcategory = category.Parent == null ? "" : category.Name;
            var row = DefaultRow(parent.Name, subcategory, parent.CategoryType.ToString());
            row.CategoryId = parent.Id.Value; row.SubcategoryId = category.Parent == null ? null : category.Id.Value;
            rows.TryAdd(row.Key, row);
            foreach (Category sub in category.SubCategories) Add(sub);
        }
        // Categories are a definition catalog, not a collector of currently drawn elements.
        foreach (Category category in document.Settings.Categories) Add(category);
        foreach (BuiltInCategory id in Enum.GetValues<BuiltInCategory>().Distinct())
        {
            if (id == BuiltInCategory.INVALID) continue;
            Category? category;
            try { category = Category.GetCategory(document, id); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { continue; }
            if (category != null) Add(category);
        }
        // Supplement Revit's special DWG keys (patterns/wall variants etc.). Never query
        // the project's saved ExportDWGSettings, and never use this table as the only source.
        using var options = new DWGExportOptions { LayerMapping = "AIA" };
        using var table = options.GetExportLayerTable();
        foreach (var pair in table)
        {
            if (pair.Value.CategoryType is LayerCategoryType.Imported or LayerCategoryType.Modifier) continue;
            if (string.IsNullOrWhiteSpace(pair.Key.CategoryName)) continue;
            var row = DefaultRow(pair.Key.CategoryName, pair.Key.SubCategoryName, pair.Value.CategoryType.ToString(), (int)pair.Key.SpecialType);
            rows.TryAdd(row.Key, row);
        }
        if (rows.Count == 0) throw new InvalidDataException("Revit 카테고리 정의를 읽지 못했습니다. 빈 기본값을 저장하지 않았습니다.");
        return Order(rows.Values);
    }

    public static RevitLayerRow DefaultRow(string category, string subcategory, string group, int specialType = -1)
    {
        string name = string.Join("_", new[] { category, subcategory }.Where(v => !string.IsNullOrWhiteSpace(v)));
        foreach (char invalid in "<>/\\\":;?*|=\r\n") name = name.Replace(invalid, '_');
        name = name.Trim(); if (name.Length == 0) name = "기본";
        if (name.Length > 235) name = name[..235];
        return new RevitLayerRow { Category = category, Subcategory = subcategory, CategoryGroup = group, SpecialType = specialType,
            Layer = name, CutLayer = name + "_절단", Color = 7, CutColor = 7,
            OriginalLayer = name, OriginalCutLayer = name + "_절단", OriginalColor = 7, OriginalCutColor = 7 };
    }

    public static List<RevitLayerRow> Order(IEnumerable<RevitLayerRow> rows) => rows
        .OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(r => r.IsCustom ? 1 : r.Subcategory.Length == 0 ? 0 : 2)
        .ThenBy(r => r.IsCustom ? "" : r.Subcategory, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.IsCustom ? 0 : r.SpecialType).ToList();
}
