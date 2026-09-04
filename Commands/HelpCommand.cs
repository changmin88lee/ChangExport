using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.App;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class HelpCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        while (true)
        {
            TaskDialogResult selected = ShowIndex();
            if (selected == TaskDialogResult.CommandLink1) ShowQuickStart();
            else if (selected == TaskDialogResult.CommandLink2) ShowLayerFilters();
            else if (selected == TaskDialogResult.CommandLink3) ShowSheetSetsAndExport();
            else if (selected == TaskDialogResult.CommandLink4) ShowSettingsAndDiagnostics();
            else break;
        }

        return Result.Succeeded;
    }

    private static TaskDialogResult ShowIndex()
    {
        var dialog = new TaskDialog("창Export 도움말 · " + ProductInfo.Version)
        {
            MainInstruction = "기능별 도움말",
            MainContent =
                "확인할 항목을 선택하세요. 각 설명을 닫으면 이 목차로 돌아옵니다.\n\n" +
                "권장 작업 순서: DWG 레이어 설정 → 시트 세트 구성 → 설정 확인 → DWG 출력 → 결과·진단 확인",
            FooterText = "창Export는 원본 RVT·원본 뷰와 Revit에 저장된 DWG 출력 설정을 직접 수정하지 않습니다.",
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "1. 빠른 시작", "처음 사용하는 프로젝트의 전체 설정 순서");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "2. 레이어·유형·재료 필터", "카테고리 매핑, 필터 판정 및 복합·단일 벽 경계 우선순위");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "3. 시트 세트·DWG 출력", "호스트·링크 시트 구성, 배치, 축척 및 최종 파일");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "4. 설정·진단·문제 확인", "전역폭, 간격, Manifest와 진단 TXT 확인 방법");
        return dialog.Show();
    }

    private static void ShowQuickStart()
    {
        ShowTopic("창Export 도움말 · 빠른 시작", "처음 출력할 때의 권장 순서",
            "1. [DWG 레이어 설정]에서 템플릿과 레이어를 준비합니다.\n" +
            "2. [시트 세트 구성]에서 출력할 시트를 하나 이상의 세트로 묶습니다.\n" +
            "3. [설정]에서 전역폭 판별 문자열과 시트 간격을 확인합니다.\n" +
            "4. [DWG 출력]에서 세트·출력 폴더를 선택하고 실행합니다.\n" +
            "5. 결과 창에서 DWG, Manifest와 경고를 확인합니다.",
            "■ 1단계 · DWG 레이어 설정\n" +
            "• 템플릿을 새로 만들거나 기존 템플릿을 복제한 뒤 카테고리별 투영·절단 레이어, ACI 색상, 선종류와 선가중치를 지정합니다.\n" +
            "• 특정 유형이나 재료만 별도 레이어로 보내려면 유형 이름 필터 또는 재료 필터를 추가합니다.\n" +
            "• 템플릿은 파일로 내보내 다른 프로젝트에서 불러올 수 있습니다. 세트에 사용 중인 템플릿은 삭제할 수 없습니다.\n\n" +
            "■ 2단계 · 시트 세트 구성\n" +
            "• 왼쪽의 호스트·링크 모델별 시트에서 필요한 항목을 선택해 세트를 만듭니다.\n" +
            "• 세트 이름, 사용할 DWG 레이어 템플릿, 가로/세로 배치 방향과 시트 순서를 정합니다.\n" +
            "• 한 세트는 하나의 최종 DWG가 됩니다. 서로 다른 모델의 시트도 한 세트에 넣을 수 있습니다.\n\n" +
            "■ 3단계 · 출력 전 확인\n" +
            "• [설정]의 시트 간격은 모든 세트에 공통 적용됩니다. 전역폭이 필요하지 않으면 판별 문자열을 비워 둡니다.\n" +
            "• [DWG 출력]에서 비어 있는 세트, 찾을 수 없는 시트, 누락된 템플릿이 표시되면 먼저 세트 구성을 고칩니다.\n\n" +
            "■ 4단계 · 결과 확인\n" +
            "• 최종 DWG는 세트 이름으로 저장되며 기존 파일이 있으면 _v2, _v3처럼 새 이름을 사용하여 덮어쓰지 않습니다.\n" +
            "• 저장 성공은 내용이 원본과 완전히 같다는 뜻이 아닙니다. 도곽, 글자, 치수, 해치, 마스킹, 잘림과 레이어를 원본 Revit 시트와 비교하세요.\n" +
            "• 경고가 있으면 결과 창의 [진단 TXT 저장]으로 파일을 만든 뒤 해당 DWG·Manifest와 함께 확인합니다.");
    }

    private static void ShowLayerFilters()
    {
        ShowTopic("창Export 도움말 · 레이어와 필터", "레이어가 결정되는 방식",
            "기본 카테고리 매핑이 먼저 적용되고, 일치한 유형 이름 또는 재료 필터가 해당 형상을 지정 레이어로 분류합니다.\n" +
            "최종 형상은 Revit 기본 DWG를 유지하며, 임시 필터 DWG는 분류 근거로만 사용합니다.",
            "■ 기본 카테고리 매핑\n" +
            "• 카테고리의 +를 펼쳐 하위 항목별 투영·절단 레이어와 색상을 설정합니다.\n" +
            "• 선종류·선가중치의 ‘원본 유지’는 Revit 기본 DWG 표현을 유지한다는 뜻입니다.\n" +
            "• 창Export 템플릿은 Revit의 기존 DWG 출력 설정과 독립적이며, 기존 설정을 자동으로 가져오지 않습니다.\n\n" +
            "■ 유형 이름 필터\n" +
            "• 선택한 카테고리 안에서 Revit 유형 이름에 입력 문자열이 포함되는지 비교합니다. 대소문자는 구분하지 않습니다.\n" +
            "• 여러 필터가 동시에 맞으면 목록의 위쪽에 있는 첫 일치 필터만 적용됩니다. 구체적인 조건을 위에 두세요.\n" +
            "• 투영과 절단의 대상 레이어·ACI 색상을 각각 지정할 수 있습니다.\n\n" +
            "■ 재료 필터\n" +
            "• 목록에서 선택한 실제 Revit 재료를 기준으로 복합벽·복합바닥의 해당 재료층을 분류합니다. 재료 이름만 같은 다른 재료가 아니라 프로젝트의 재료 연결 상태를 확인하세요.\n" +
            "• 호스트와 로드된 링크의 복합벽·복합바닥은 임시 Part와 식별색으로 판정하고, 작업 후 원본에 남기지 않습니다.\n" +
            "• 같은 복합객체 안의 공유 경계는 벽의 바깥→안쪽, 바닥의 위→아래 순서에서 더 안쪽·아래쪽 레이어가 한 번만 소유합니다. 따라서 맞닿은 재료층 사이에 중복 경계가 생기지 않습니다.\n\n" +
            "■ 서로 붙인 단일 벽의 경계 우선순위\n" +
            "• 서로 다른 단일 벽이 정확히 같은 선에서 맞닿으면 복합구조 기능 우선순위로 경계 소유자를 하나만 정합니다.\n" +
            "• 구조 > 하지재 > 열/공기층 > 마감 2 > 마감 1 > 멤브레인층 순입니다.\n" +
            "• 기능이 같으면 어느 한쪽만 남으며 특정 객체가 항상 우선한다고 보장하지 않습니다. 실제 틈이나 겹침이 있으면 서로 다른 선이므로 둘 다 출력될 수 있습니다.\n\n" +
            "■ Native Geometry Engine(NGE)\n" +
            "• Revit 기본 시트 DWG가 유일한 최종 형상 원본입니다. 유형·재료 필터용 임시 DWG는 정확히 일치하는 Native 선·폴리라인·해치의 레이어 분류에만 사용합니다.\n" +
            "• 유형 필터와 재료 필터는 별도 경로로 판정됩니다. 필터가 실패하면 기본 카테고리 출력은 보존하고 결과 경고와 Manifest에 실패 내용을 남깁니다.\n" +
            "• 기존 CAD_LAYER와 구형 Rule은 현재 출력 경로에 연결하지 않습니다.\n\n" +
            "■ 필터가 예상과 다를 때\n" +
            "• 유형 문자열, 대상 카테고리, 필터 순서, 재료 연결, 복합구조 기능과 실제 모델 간격을 먼저 확인합니다.\n" +
            "• 일부 선만 기본 ‘벽 마감 절단’ 등으로 남으면 진단의 NativeOverlayRuleDiagnostics, SourceLayers, 일치·모호·분할 선 개수와 필터 경고를 확인합니다.");
    }

    private static void ShowSheetSetsAndExport()
    {
        ShowTopic("창Export 도움말 · 시트 세트와 출력", "여러 시트를 하나의 모형공간 DWG로 출력",
            "한 세트의 시트를 지정 순서와 방향으로 모형공간에 배치하여 DWG 한 개로 저장합니다.\n" +
            "호스트 시트와 현재 로드된 Revit 링크 시트를 함께 구성할 수 있습니다.",
            "■ 시트 원본\n" +
            "• 호스트 모델, 로드된 로컬·네트워크 RVT 링크와 로드된 중첩 링크의 시트를 읽습니다. 모델별 목록 순서와 검색을 이용해 시트를 찾을 수 있습니다.\n" +
            "• 링크 시트는 원본 링크를 저장하지 않고 일회용 로컬 사본에서 처리한 뒤 닫고 정리합니다.\n" +
            "• 로드되지 않은 링크 또는 직접 복제할 수 없는 클라우드 링크는 출력할 수 없으며, 실행 전에 해당 원인을 표시합니다.\n\n" +
            "■ 세트 구성\n" +
            "• 선택한 시트로 세트를 만든 뒤 세트 이름, 레이어 템플릿, 가로/세로 방향과 구성원 순서를 저장합니다.\n" +
            "• 한 세트 안의 모든 시트는 같은 레이어 템플릿을 사용해야 합니다. 시트가 이동·삭제되거나 링크가 언로드되면 세트를 다시 확인하세요.\n" +
            "• [설정]의 공통 간격은 축척 변환 후 모형공간 mm 기준으로 가로·세로 배치에 모두 적용됩니다. 0 mm이면 시트 바깥 경계를 붙이고 도곽 안쪽 여백은 유지합니다.\n\n" +
            "■ 축척과 파일 형식\n" +
            "• 시트별 가장 큰 2D 뷰의 축척을 기준으로 도곽까지 모형공간 mm로 확대하고, 같은 시트 안 다른 뷰의 상대 축척은 유지합니다.\n" +
            "• 최종 파일은 DWG 2010, 모형공간, 밀리미터 단위로 저장하며 저장 직후 다시 열어 파일 구조를 검사합니다.\n" +
            "• 세트 이름에 파일명으로 쓸 수 없는 문자가 있으면 _로 바꿉니다. 같은 파일명이 이미 있으면 번호를 붙여 기존 DWG를 보존합니다.\n\n" +
            "■ 형상 처리\n" +
            "• 별도 뷰 DWG와 뷰포트 내용을 내장 엔진으로 결합하므로 외부 AutoCAD나 별도 CAD 프로그램은 필요하지 않습니다.\n" +
            "• 고정 형상의 로드 가능 패밀리와 Revit 상세 그룹은 가능한 경우 공유 DWG 블록으로 유지합니다.\n" +
            "• 구조 보·기둥, 커튼월·난간 구성요소, 내부 작성·선형·스케치·2레벨·적응형 패밀리는 안전을 위해 개별 객체로 유지될 수 있습니다.\n" +
            "• 원근·음영 뷰는 생략될 수 있고 이미지는 사각형 경계로 대체됩니다. 원본 시트와 실제 결과 비교가 필요합니다.\n\n" +
            "■ 취소·부분 실패\n" +
            "• 취소하면 진행 중 세트의 최종 파일은 만들지 않습니다. 앞서 완료된 세트는 결과 목록에 남습니다.\n" +
            "• 한 세트가 실패해도 가능한 다음 세트는 계속 처리하며, 단계명과 상세 오류를 결과에 기록합니다.");
    }

    private static void ShowSettingsAndDiagnostics()
    {
        ShowTopic("창Export 도움말 · 설정과 진단", "설정 의미와 문제를 확인하는 위치",
            "[설정]은 현재 프로젝트에 저장됩니다.\n" +
            "[기술 진단]은 환경·출력 설정 상태를 요약하고, 실제 출력의 상세 증거는 결과 창의 Manifest와 진단 TXT에 남습니다.",
            "■ 전역폭 판별 문자열\n" +
            "• Revit 선 스타일 이름에 지정 문자열이 포함되면 해당 선을 전역폭 폴리선으로 변환합니다. 기본 문자열을 사용할 때는 대상 선 스타일 이름에 그 문자열이 실제로 포함되어야 합니다.\n" +
            "• 폭은 Revit 기본 DWG의 선가중치와 시트 축척으로 계산합니다. 빈 값이면 전역폭 변환을 하지 않습니다.\n\n" +
            "■ 시트 배치 간격\n" +
            "• 모든 세트에 공통이며 모형공간 mm 기준입니다. 0 mm는 도곽 외곽끼리 붙이는 값이지 시트 내부 여백을 제거하는 값이 아닙니다.\n\n" +
            "■ 기술 진단\n" +
            "• 현재 애드인·Revit 버전, 모델, 창Export 출력 설정 수, 레이어 매핑 수, 시트 수와 내장 DWG 엔진을 빠르게 확인합니다.\n" +
            "• 설정 파일 경로도 표시하므로 템플릿·세트 저장 상태를 확인할 때 사용합니다. 기술 진단만으로 실제 DWG 형상 품질을 판정할 수는 없습니다.\n\n" +
            "■ Manifest JSON\n" +
            "• 출력할 때마다 출력 폴더에 ChangExport_Manifest_날짜_작업ID.json을 만듭니다. 애드인 버전, 템플릿, 필터 수, 세트 구성, 시트 원본, 경고, 단계별 처리 시간과 형상 엔진 통계를 기록합니다.\n" +
            "• filterMatches는 Revit에서 필터 조건에 맞은 요소 수이고, CustomRuleEntityCounts와 NativeOverlay 관련 값은 최종 DWG에 실제 반영된 객체·형상 판정 결과입니다. 두 값은 의미가 다릅니다.\n\n" +
            "■ 진단 TXT 저장\n" +
            "• 출력 결과 창의 [진단 TXT 저장]은 성공·실패, 세트별 메시지, 경고, 오류 상세, 시트 진단과 처리 시간을 읽기 쉬운 텍스트로 저장합니다.\n" +
            "• 문제를 전달할 때는 진단 TXT와 문제가 보이는 DWG 화면, 원래 기대한 레이어 이름, 호스트/링크 여부, 벽·바닥의 복합구조 또는 단일 객체 여부를 함께 알려 주세요.\n\n" +
            "■ 자주 보는 경고\n" +
            "• ‘필터 미반영’: 기본 카테고리 DWG는 만들었지만 해당 유형·재료 필터를 적용하지 못했다는 뜻입니다.\n" +
            "• ‘도면 내용 확인’: 저장은 됐지만 배치 뷰 결합이나 내용 완전성을 원본과 비교해야 한다는 뜻입니다.\n" +
            "• ‘일치하지 않음/모호함’: 임시 분류 형상과 Native 형상을 안전하게 하나로 확정하지 못해 원본 레이어를 유지했을 수 있다는 뜻입니다.\n\n" +
            "■ 원본 보존 범위\n" +
            "• 원본 RVT, 원본 시트·뷰, 링크 RVT와 Revit의 기존 DWG 출력 설정을 직접 변경하지 않습니다. 임시 복제 뷰·필터·Part는 성공, 실패와 취소 시 정리하도록 처리합니다.\n" +
            "• 설정과 출력 결과는 별개입니다. 설정이 저장됐더라도 최종 DWG 반영 여부는 Manifest와 실제 DWG에서 확인하세요.");
    }

    private static void ShowTopic(string title, string instruction, string content, string details)
    {
        var dialog = new TaskDialog(title)
        {
            MainInstruction = instruction,
            MainContent = content,
            ExpandedContent = details,
            FooterText = "이 창을 닫으면 기능별 도움말 목차로 돌아갑니다.",
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dialog.Show();
    }
}
