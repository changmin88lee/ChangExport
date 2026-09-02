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
                "1. DWG 레이어 설정에서 +로 카테고리를 펼쳐 색상 지정, 필요 시 유형 이름 포함 필터 추가\n" +
                "2. 호스트·링크 시트 세트 구성에서 Ctrl/Shift 선택 후 통합 세트 생성\n" +
                "3. 세트의 순서·가로/세로 방향 저장\n" +
                "4. 설정에서 전역폭 판별 문자열·세트별 시트 간격 저장\n" +
                "5. DWG 출력에서 출력 설정·세트·폴더 선택\n" +
                "6. 최종 DWG 모형공간과 Manifest 확인",
            ExpandedContent =
                "Revit 2026만 필요하며 외부 CAD 프로그램을 설치하거나 실행하지 않습니다. DWG 처리 모듈은 애드인 DLL에 포함됩니다. 창Export의 기본 카테고리 목록과 독립 출력 설정을 사용합니다. Revit에 저장된 출력 설정은 자동으로 불러오지 않습니다. " +
                "시트별 가장 큰 2D 뷰의 축척으로 도곽까지 확대하고, 다른 뷰의 상대 축척은 유지합니다. 간격 기본값은 가로·세로 모두 0 mm입니다. " +
                "DWG 2010으로 저장합니다. 원근·음영 뷰는 생략하고 이미지는 사각형으로 대체합니다. 별도 뷰 DWG는 내장 엔진에서 결합합니다. 저장 여부와 내용 완전성은 다르므로 글자·치수·해치·잘림은 원본 시트와 비교하세요. " +
                "로드된 로컬·네트워크 RVT 링크와 중첩 링크의 시트를 호스트 시트와 함께 표시하고, 서로 다른 모델의 시트를 한 세트에 원하는 순서로 묶을 수 있습니다. 링크 시트는 원본을 수정하지 않는 임시 사본에서 출력한 뒤 즉시 닫고 정리합니다. 로드되지 않은 링크와 현재 직접 복제할 수 없는 클라우드 링크는 출력 전에 정확한 오류를 표시합니다. " +
                "카테고리 아래 필터는 호스트와 로드된 링크의 유형 이름에 포함된 문자를 대소문자 구분 없이 비교하며 위에서 첫 일치만 적용합니다. 독립 복제 시트·뷰의 네이티브 뷰 필터로 링크까지 전파한 뒤 복구합니다. 링크 복합재료는 호스트에 만든 임시 링크 Part의 실제 재료 ID에 고유 식별색을 부여하여 DWG 레이어로 변환하며, 링크별 미지원 사항은 다른 필터 출력을 중단하지 않고 알립니다. 창Export가 객체 재지정을 유지하여 필터를 적용합니다. 필터 미반영 시 기본 출력과 경고를 남깁니다. 기존 CAD_LAYER와 구형 Rule은 연결하지 않습니다. " +
                "최종 객체 색상은 필터 반영 후 창Export의 레이어 색상을 따릅니다. 검색 중 +로 펼치면 일치하는 하위 항목만 표시합니다. " +
                "고정 형상의 로드 가능 패밀리와 Revit 상세 그룹은 자동으로 DWG 공유 블록이 됩니다. 구조 보·기둥, 커튼월·난간 구성요소와 내부 작성·선형·스케치·2레벨·적응형 패밀리는 개별 객체로 유지합니다. " +
                "새 출력 설정·복제·이름 변경을 지원하며, 파일로 저장한 설정은 다른 프로젝트에서 불러올 수 있습니다. 원본 RVT와 Revit 출력 설정은 변경하지 않습니다. 최종 DWG는 저장 후 재열기 검사를 수행하며 처리 시간은 Manifest에 기록합니다.",
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
