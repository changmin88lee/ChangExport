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
                "4. DWG 출력에서 출력 설정·세트·폴더 선택\n" +
                "5. 최종 DWG 모형공간과 Manifest 확인",
            ExpandedContent =
                "Revit 2026만 필요하며 외부 CAD 프로그램을 설치하거나 실행하지 않습니다. DWG 처리 모듈은 애드인 DLL에 포함됩니다. 창Export의 기본 카테고리 목록과 독립 출력 설정을 사용합니다. Revit에 저장된 출력 설정은 자동으로 불러오지 않습니다. " +
                "시트별 가장 큰 2D 뷰의 축척으로 도곽까지 확대하고, 다른 뷰의 상대 축척은 유지합니다. 간격 기본값은 가로·세로 모두 0 mm입니다. " +
                "DWG 2010으로 저장합니다. 원근·음영 뷰는 생략하고 이미지는 사각형으로 대체합니다. 별도 뷰 DWG는 내장 엔진에서 결합합니다. 저장 여부와 내용 완전성은 다르므로 글자·치수·해치·잘림은 원본 시트와 비교하세요. " +
                "카테고리 아래 필터는 현재 프로젝트의 유형 이름에 포함된 문자를 대소문자 구분 없이 비교하며 위에서 첫 일치만 적용합니다. 독립 복제 시트·뷰에서 적용 후 복구하며, 링크 내부 객체·공유 뷰·그래픽 재지정 미지원 뷰는 제외 내역을 알립니다. 창Export가 객체 재지정을 유지하여 필터를 적용합니다. 필터 미반영 시 기본 출력과 경고를 남깁니다. 기존 CAD_LAYER와 구형 Rule은 연결하지 않습니다. " +
                "새 출력 설정·복제·이름 변경을 지원하며, 파일로 저장한 설정은 다른 프로젝트에서 불러올 수 있습니다. 원본 RVT와 Revit 출력 설정은 변경하지 않습니다. 최종 DWG는 저장 후 재열기 검사를 수행하며 처리 시간은 Manifest에 기록합니다.",
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
