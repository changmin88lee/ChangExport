using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.App;
using ChangExport.DwgProcessing;
using ChangExport.Standards;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class DwgPrototypeDiagnosticsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            Document document = commandData.Application.ActiveUIDocument.Document;
            var store = ExportConfigurationStore.ForDocument(document); var config = store.Load();
            var mapping = new RevitLayerMappingService(document);
            bool setupExists = mapping.SetupNames.Contains(config.SelectedSetup);
            string status = setupExists ? $"매핑 {mapping.Read(config.SelectedSetup, config).Count:N0}개" : "저장된 Revit 출력 설정이 없어 재선택 필요";
            TaskDialog.Show("창Export 기술 진단", $"{ProductInfo.Version}\n모델: {document.Title}\nRevit: {document.Application.VersionNumber}\n\n" +
                $"Revit 출력 설정: {mapping.SetupNames.Count - 1}개 + 기본값\n{status}\n시트: {SheetSetService.ReadSheets(document).Count}개\n" +
                $"DWG 엔진: {ManagedDwgProcessor.EngineName}\n외부 CAD 프로그램: 설치/실행 불필요\n\n" +
                "현재 출력: Revit 기본 카테고리 매핑 → 내장 엔진 시트 변환 → 세트별 모형공간 병합\n" +
                "커스텀 필터: 다음 단계 / 기존 CAD_LAYER·Rule은 이번 출력에 미적용\n" +
                "실제 Revit 시트의 글자·치수·해치·잘림·축척은 출력 결과 비교가 필요합니다.\n\n설정 파일: " + store.FilePath);
            return Result.Succeeded;
        }
        catch (Exception ex) { message = ex.Message; return Result.Failed; }
    }
}
