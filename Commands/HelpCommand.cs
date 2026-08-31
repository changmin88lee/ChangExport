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
                "1. DWG 레이어 설정에서 Revit 카테고리·색상 지정, 필요 시 유형 이름 포함 필터 추가\n" +
                "2. 시트 세트 구성에서 Ctrl/Shift 선택 후 세트 생성\n" +
                "3. 세트의 순서·가로/세로 방향·간격 저장\n" +
                "4. 회사 DWG 출력에서 세트와 폴더 선택\n" +
                "5. 최종 DWG 모형공간과 Manifest 확인",
            ExpandedContent =
                "Revit 2026만 필요하며 외부 CAD 프로그램을 설치하거나 실행하지 않습니다. DWG 처리 모듈은 애드인 DLL에 포함됩니다. Revit 기본 DWG 매핑을 바탕으로 시트당 변환 후 세트별 하나의 모형공간 DWG를 만듭니다. " +
                "시트 지면 mm와 시트의 상대 축척을 유지하며 각 뷰를 실물 1:1로 바꾸지는 않습니다. " +
                "DWG 2010으로 저장합니다. 원근·음영 뷰는 생략하고 이미지는 사각형으로 대체합니다. 별도 뷰 DWG는 내장 엔진에서 결합합니다. 저장 여부와 내용 완전성은 다르므로 글자·치수·해치·잘림은 원본 시트와 비교하세요. " +
                "카테고리 아래 필터는 현재 프로젝트의 유형 이름에 포함된 문자를 대소문자 구분 없이 비교하며 위에서 첫 일치만 적용합니다. 독립 복제 시트·뷰에서 적용 후 복구하며, 링크 내부 객체·공유 뷰·그래픽 재지정 미지원 뷰는 제외 내역을 알립니다. Revit 출력 설정은 객체 재지정을 유지해야 합니다. 필터 미반영 시 기본 출력과 경고를 남깁니다. 기존 CAD_LAYER와 구형 Rule은 연결하지 않습니다. " +
                "출력 설정은 프로젝트별 외부 파일에 저장되며 원본 RVT·Revit Setup·기존 Profile은 변경하지 않습니다.",
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
