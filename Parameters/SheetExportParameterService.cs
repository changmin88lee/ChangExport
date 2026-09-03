using Autodesk.Revit.DB;

namespace ChangExport.Parameters;

public sealed class SheetExportParameterService
{
    public string GetGroup(ViewSheet sheet) =>
        Get(sheet, ParameterDefinitions.ExportGroupGuid, ParameterDefinitions.ExportGroupName)?.AsString()?.Trim()
        ?? string.Empty;

    public int? GetOrder(ViewSheet sheet)
    {
        Parameter? parameter = Get(sheet, ParameterDefinitions.ExportOrderGuid, ParameterDefinitions.ExportOrderName);
        return parameter is null || !parameter.HasValue ? null : parameter.AsInteger();
    }

    private static Parameter? Get(Element element, Guid guid, string name) =>
        element.get_Parameter(guid) ?? element.LookupParameter(name);
}
