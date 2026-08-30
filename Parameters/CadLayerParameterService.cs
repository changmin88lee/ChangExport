using Autodesk.Revit.DB;

namespace ChangExport.Parameters;

public sealed class CadLayerParameterService
{
    public Parameter? GetParameter(Element element) =>
        element.get_Parameter(ParameterDefinitions.CadLayerGuid)
        ?? element.LookupParameter(ParameterDefinitions.CadLayerName);

    public string GetValue(Element element) => GetParameter(element)?.AsString()?.Trim() ?? string.Empty;

    public ParameterWriteResult SetValue(IEnumerable<Element> elements, string value)
    {
        var result = new ParameterWriteResult();
        foreach (Element element in elements)
        {
            Parameter? parameter = GetParameter(element);
            if (parameter is null)
            {
                result.UnsupportedIds.Add(element.Id.Value);
                continue;
            }

            if (parameter.IsReadOnly)
            {
                result.ReadOnlyIds.Add(element.Id.Value);
                continue;
            }

            try
            {
                if (parameter.Set(value)) result.SuccessIds.Add(element.Id.Value);
                else result.FailedIds.Add(element.Id.Value);
            }
            catch
            {
                result.FailedIds.Add(element.Id.Value);
            }
        }
        return result;
    }
}

public sealed class ParameterWriteResult
{
    public List<long> SuccessIds { get; } = new();
    public List<long> UnsupportedIds { get; } = new();
    public List<long> ReadOnlyIds { get; } = new();
    public List<long> FailedIds { get; } = new();
}
