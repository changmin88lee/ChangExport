using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Parameters;
using ChangExport.Standards;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class DwgPrototypeDiagnosticsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDocument = commandData.Application.ActiveUIDocument;
        Document document = uiDocument.Document;
        try
        {
            var parameters = new SharedParameterService();
            var repository = new CadStandardRepository();
            var profile = repository.LoadActive();
            var sheetService = new SheetExportParameterService();
            List<ViewSheet> sheets = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(x => !x.IsPlaceholder).ToList();
            int grouped = sheets.Count(x => !string.IsNullOrWhiteSpace(sheetService.GetGroup(x)));
            int selected = uiDocument.Selection.GetElementIds().Count;
            int setupCount = BaseExportOptions.GetPredefinedSetupNames(document).Count;

            string report =
                $"모델: {document.Title}\n" +
                $"Revit: {document.Application.VersionNumber}\n" +
                $"활성 View: {uiDocument.ActiveView.Name}\n\n" +
                $"CAD_LAYER Binding: {Mark(parameters.HasCadLayerBinding(document))}\n" +
                $"CAD_EXPORT_GROUP Binding: {Mark(parameters.HasExportGroupBinding(document))}\n" +
                $"CAD_EXPORT_ORDER Binding: {Mark(parameters.HasExportOrderBinding(document))}\n\n" +
                $"선택 객체: {selected:N0}개\n" +
                $"Sheet: {sheets.Count:N0}개 (그룹 지정 {grouped:N0})\n" +
                $"DWG Export Setup: {setupCount:N0}개\n" +
                $"Profile: {profile.ProfileName} (Layer {profile.Layers.Count}, Rule {profile.Rules.Count})\n" +
                $"Profile 검사: {(repository.Validate(profile).Count == 0 ? "정상" : "확인 필요")}\n\n" +
                "Beta 기술 상태\n" +
                "- Native Sheet DWG Export: 구현\n" +
                "- CAD_LAYER / Rule 판정: 구현\n" +
                "- 임시 객체별 DWG Layer 분리: 기술 게이트 대기\n" +
                "- DWG Layer Remap/Merge: SDK 미연결\n" +
                "- Sheet Model Space 평면화/병합: SDK 미연결";

            TaskDialog.Show("창Export 기술 진단", report);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static string Mark(bool value) => value ? "정상" : "준비 필요";
}
