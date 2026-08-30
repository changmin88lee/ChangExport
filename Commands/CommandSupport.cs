using Autodesk.Revit.DB;
using ChangExport.Parameters;

namespace ChangExport.Commands;

internal static class CommandSupport
{
    public static void EnsureBindings(Document document)
    {
        var service = new SharedParameterService();
        if (service.HasCadLayerBinding(document)
            && service.HasExportGroupBinding(document)
            && service.HasExportOrderBinding(document))
            return;

        using var transaction = new Transaction(document, "창Export 공유 매개변수 준비");
        transaction.Start();
        service.EnsureBindings(document);
        transaction.Commit();
    }

    public static string BuildWriteSummary(ParameterWriteResult result) =>
        $"성공 {result.SuccessIds.Count}개\n" +
        $"미지원 {result.UnsupportedIds.Count}개\n" +
        $"읽기 전용 {result.ReadOnlyIds.Count}개\n" +
        $"실패 {result.FailedIds.Count}개";
}
