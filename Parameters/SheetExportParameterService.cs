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

    public void Set(ViewSheet sheet, string group, int? order)
    {
        Parameter groupParameter = Get(sheet, ParameterDefinitions.ExportGroupGuid, ParameterDefinitions.ExportGroupName)
            ?? throw new InvalidOperationException($"{ParameterDefinitions.ExportGroupName} 매개변수가 없습니다.");
        Parameter orderParameter = Get(sheet, ParameterDefinitions.ExportOrderGuid, ParameterDefinitions.ExportOrderName)
            ?? throw new InvalidOperationException($"{ParameterDefinitions.ExportOrderName} 매개변수가 없습니다.");

        if (groupParameter.IsReadOnly || orderParameter.IsReadOnly)
            throw new InvalidOperationException("시트 그룹 매개변수가 읽기 전용입니다.");

        groupParameter.Set(group?.Trim() ?? string.Empty);
        if (order.HasValue) orderParameter.Set(order.Value);
        else orderParameter.Set(0);
    }

    private static Parameter? Get(Element element, Guid guid, string name) =>
        element.get_Parameter(guid) ?? element.LookupParameter(name);
}
