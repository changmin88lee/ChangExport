using Autodesk.Revit.DB;
using ChangExport.Models;
using ChangExport.Parameters;

namespace ChangExport.Standards;

public static class SheetSetService
{
    public static List<SheetDescriptor> ReadSheets(Document document) => new FilteredElementCollector(document)
        .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder)
        .OrderBy(s => s.SheetNumber, StringComparer.CurrentCultureIgnoreCase)
        .Select(s => new SheetDescriptor(s.UniqueId, s.Id.Value, s.SheetNumber, s.Name)).ToList();

    public static List<SheetSetDefinition> ReadSets(Document document, RevitExportConfiguration config, IReadOnlyList<SheetDescriptor> sheets)
    {
        var sets = config.SheetSets.Select(s => s.Copy()).ToList();
        if (sets.Count == 0)
        {
            // Read the previous group/order values only. No binding preparation or model writes.
            var legacy = new SheetExportParameterService();
            foreach (var group in sheets.GroupBy(s => legacy.GetGroup((ViewSheet)document.GetElement(s.UniqueId))).Where(g => g.Key.Length > 0))
                sets.Add(new SheetSetDefinition { Name = group.Key,
                    SheetUniqueIds = group.OrderBy(s => legacy.GetOrder((ViewSheet)document.GetElement(s.UniqueId)) ?? 0).Select(s => s.UniqueId).ToList() });
        }
        var assigned = sets.SelectMany(s => s.SheetUniqueIds).ToList();
        if (assigned.Distinct().Count() != assigned.Count) throw new InvalidDataException("저장된 세트에 중복 시트가 있습니다. 출력 설정을 확인하세요.");
        foreach (var sheet in sheets.Where(s => !assigned.Contains(s.UniqueId)))
            sets.Add(new SheetSetDefinition { Name = sheet.Number, SheetUniqueIds = new() { sheet.UniqueId } });
        foreach (var set in sets)
        {
            var assignedTemplates = set.SheetUniqueIds.Select(id => config.SheetTemplateIds.GetValueOrDefault(id, string.Empty))
                .Distinct(StringComparer.Ordinal).ToList();
            set.TemplateId = assignedTemplates.Count == 1 ? assignedTemplates[0] : string.Empty;
        }
        return sets;
    }

    public static void ApplyAssignments(RevitExportConfiguration config, IEnumerable<SheetSetDefinition> sets,
        IReadOnlyDictionary<string, string> assignments)
    {
        config.SheetSets = sets.Select(s => s.Copy()).ToList();
        config.SheetTemplateIds = new Dictionary<string, string>(assignments, StringComparer.Ordinal);
        foreach (var set in config.SheetSets)
        {
            var ids = set.SheetUniqueIds.Select(id => config.SheetTemplateIds.GetValueOrDefault(id, string.Empty))
                .Distinct(StringComparer.Ordinal).ToList();
            set.TemplateId = ids.Count == 1 ? ids[0] : string.Empty;
        }
    }
}
