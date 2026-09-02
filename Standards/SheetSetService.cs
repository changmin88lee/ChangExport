using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using ChangExport.Models;
using ChangExport.Parameters;

namespace ChangExport.Standards;

public static class SheetSetService
{
    public sealed record SheetSource(SheetDescriptor Sheet, Document Document);

    public static List<SheetDescriptor> ReadSheets(Document document) => ReadSheetSources(document).Values
        .Select(source => source.Sheet)
        .OrderBy(sheet => sheet.IsHost ? 0 : 1)
        .ThenBy(sheet => sheet.SourceName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(sheet => sheet.Number, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static Dictionary<string, SheetSource> ReadSheetSources(Document host)
    {
        var result = new Dictionary<string, SheetSource>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Read(Document owner, bool isHost, int depth)
        {
            string sourceKey = isHost ? "" : SourceKey(owner);
            string visitKey = isHost ? "host" : sourceKey;
            if (!visited.Add(visitKey)) return;
            string sourceName = ModelName(owner);
            foreach (ViewSheet sheet in new FilteredElementCollector(owner).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(sheet => !sheet.IsPlaceholder))
            {
                var descriptor = new SheetDescriptor(sheet.UniqueId, sheet.Id.Value, sheet.SheetNumber, sheet.Name,
                    sourceKey, sourceName, isHost, owner.PathName ?? "", depth);
                result[descriptor.Key] = new SheetSource(descriptor, owner);
            }
            foreach (RevitLinkInstance link in new FilteredElementCollector(owner).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                if (link.GetLinkDocument() is { } linked) Read(linked, false, depth + 1);
        }
        Read(host, true, 0);
        return result;
    }

    public static string SourceKey(Document document)
    {
        // A copied/Save-As model can retain ProjectInformation.UniqueId. Include
        // its normalized source location so separate linked RVTs never share a key.
        string projectIdentity = document.ProjectInformation?.UniqueId ?? "";
        string sourceLocation = document.PathName;
        if (string.IsNullOrWhiteSpace(sourceLocation)) sourceLocation = document.Title;
        else
        {
            try { sourceLocation = Path.GetFullPath(sourceLocation); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Revit Server/cloud display paths are valid document identities
                // even when System.IO cannot normalize them as Windows paths.
            }
        }
        sourceLocation = sourceLocation.Trim().ToUpperInvariant();
        string identity = projectIdentity + "|" + sourceLocation;
        return "link:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string ModelName(Document document)
    {
        string name = document.Title;
        if (!string.IsNullOrWhiteSpace(document.PathName))
        {
            string pathName = Path.GetFileNameWithoutExtension(document.PathName);
            if (!string.IsNullOrWhiteSpace(pathName)) name = pathName;
        }
        return string.IsNullOrWhiteSpace(name) ? "이름 없는 모델" : name;
    }

    public static List<SheetSetDefinition> ReadSets(Document document, RevitExportConfiguration config, IReadOnlyList<SheetDescriptor> sheets)
    {
        var sets = config.SheetSets.Select(s => s.Copy()).ToList();
        if (sets.Count == 0)
        {
            // Read the previous group/order values only. No binding preparation or model writes.
            var legacy = new SheetExportParameterService();
            foreach (var group in sheets.Where(sheet => sheet.IsHost)
                .GroupBy(s => legacy.GetGroup((ViewSheet)document.GetElement(s.UniqueId))).Where(g => g.Key.Length > 0))
                sets.Add(new SheetSetDefinition { Name = group.Key,
                    SheetUniqueIds = group.OrderBy(s => legacy.GetOrder((ViewSheet)document.GetElement(s.UniqueId)) ?? 0).Select(s => s.Key).ToList() });
        }
        var assigned = sets.SelectMany(s => s.SheetUniqueIds).ToList();
        if (assigned.Distinct().Count() != assigned.Count) throw new InvalidDataException("저장된 세트에 중복 시트가 있습니다. 출력 설정을 확인하세요.");
        foreach (var sheet in sheets.Where(s => !assigned.Contains(s.Key)))
            sets.Add(new SheetSetDefinition { Name = sheet.DisplayNumber, SheetUniqueIds = new() { sheet.Key } });
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
            set.MarginMm = 0;
            foreach (string id in set.SheetUniqueIds)
                if (string.IsNullOrWhiteSpace(set.TemplateId)) config.SheetTemplateIds.Remove(id);
                else config.SheetTemplateIds[id] = set.TemplateId;
        }
    }
}
