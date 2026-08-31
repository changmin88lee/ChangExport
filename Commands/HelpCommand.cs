using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class HelpCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var dialog = new TaskDialog("창Export " + ChangExport.App.ProductInfo.Version)
        {
            MainInstruction = "시트 세트 · 모형공간 DWG 출력",
            MainContent =
                "1. DWG 레이어 설정에서 Revit 출력 설정과 카테고리별 색상 확인\n" +
                "2. 시트 세트 구성에서 Ctrl/Shift 선택 후 세트 생성\n" +
                "3. 세트의 순서·가로/세로 방향·간격 저장\n" +
                "4. 회사 DWG 출력에서 세트와 폴더 선택\n" +
                "5. 최종 DWG 모형공간과 Manifest 확인",
            ExpandedContent =
                "Revit 2026만 필요하며 외부 CAD 프로그램을 설치하거나 실행하지 않습니다. DWG 처리 모듈은 애드인 DLL에 포함됩니다. Revit 기본 DWG 매핑을 바탕으로 시트당 변환 후 세트별 하나의 모형공간 DWG를 만듭니다. " +
                "시트 지면 mm와 시트의 상대 축척을 유지하며 각 뷰를 실물 1:1로 바꾸지는 않습니다. " +
                "DWG 2010 이상과 2D 뷰포트를 지원하며 원근·음영·지원하지 않는 객체는 실패 사유를 표시합니다. 구버전 DWG는 한글 보존을 보장할 수 없어 저장하지 않습니다. 글자·치수·해치·잘림은 실제 출력 결과를 비교하세요. 커스텀 필터는 다음 단계이며 기존 CAD_LAYER와 Rule은 이번 출력에 적용하지 않습니다. " +
                "출력 설정은 프로젝트별 외부 파일에 저장되며 원본 RVT·Revit Setup·기존 Profile은 변경하지 않습니다.",
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
