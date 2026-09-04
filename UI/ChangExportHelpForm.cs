using System.Drawing;
using System.Windows.Forms;
using ChangExport.App;

namespace ChangExport.UI;

public sealed class ChangExportHelpForm : Form
{
    private sealed record HelpTopic(string Overview, string Steps, string Details);

    public ChangExportHelpForm()
    {
        UiTheme.Apply(this);
        Text = $"창Export - 도움말 · {ProductInfo.Version}";
        Width = 1080;
        Height = 820;
        MinimumSize = new Size(920, 720);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(18, 6),
            Multiline = true,
            Font = new Font("맑은 고딕", 9.5F)
        };

        foreach ((string title, HelpTopic topic) in CreateTopics())
            tabs.TabPages.Add(CreateHelpTab(title, topic));

        var closeButton = UiTheme.PrimaryButton("닫기");
        closeButton.Width = 96;
        closeButton.DialogResult = DialogResult.OK;

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(0, 12, 18, 12),
            BackColor = Color.White
        };
        footer.Controls.Add(closeButton);
        footer.Resize += (_, _) =>
        {
            closeButton.Left = footer.ClientSize.Width - closeButton.Width - 18;
            closeButton.Top = 12;
        };

        Controls.Add(tabs);
        Controls.Add(footer);
        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    private static IReadOnlyList<(string Title, HelpTopic Topic)> CreateTopics() =>
    [
        ("시작하기", new HelpTopic(
            "창Export는 Revit 시트를 세트로 묶어 하나의 모형공간 DWG로 출력하는 Revit 2026 애드인입니다. " +
            "호스트와 현재 로드된 Revit 링크의 시트를 함께 구성할 수 있으며, 외부 AutoCAD 실행 없이 내장 엔진으로 처리합니다.\r\n\r\n" +
            "원본 RVT·원본 시트와 뷰·링크 RVT·Revit에 저장된 기존 DWG 출력 설정은 직접 수정하지 않습니다. 창Export의 레이어 템플릿과 시트 세트는 현재 프로젝트용 설정으로 별도 저장됩니다.",
            "1. [DWG 레이어 설정]에서 새 템플릿을 만들거나 기존 템플릿을 복제합니다.\r\n\r\n" +
            "2. 카테고리별 투영·절단 레이어와 색상을 확인하고 필요한 유형 이름·재료 필터를 추가합니다.\r\n\r\n" +
            "3. [시트 세트 구성]에서 호스트·링크 시트를 선택해 세트를 만들고 템플릿, 방향과 순서를 저장합니다.\r\n\r\n" +
            "4. [설정]에서 전역폭 판별 문자열과 공통 시트 간격을 확인합니다.\r\n\r\n" +
            "5. [DWG 출력]에서 세트와 출력 폴더를 선택해 실행합니다.\r\n\r\n" +
            "6. 결과 창에서 DWG와 경고를 확인하고, Manifest 또는 [진단 TXT 저장]으로 실제 반영 상태를 점검합니다.",
            "[권장 검토 순서]\r\n" +
            "먼저 작은 대표 시트 한 장으로 레이어·글자·치수·해치·마스킹·잘림을 확인한 뒤 여러 시트를 묶는 것이 안전합니다. 저장 성공은 파일 구조가 정상이라는 뜻이며 원본 시트와 시각적으로 완전히 같다는 뜻은 아닙니다.\r\n\r\n" +
            "[결과 파일]\r\n" +
            "한 세트는 최종 DWG 한 개가 됩니다. 세트 이름을 파일명으로 사용하며 사용할 수 없는 문자는 _로 바꿉니다. 같은 이름이 이미 있으면 _v2, _v3처럼 새 이름을 사용하여 기존 DWG를 덮어쓰지 않습니다.\r\n\r\n" +
            "[현재 연결 범위]\r\n" +
            "기본 카테고리 매핑, 유형 이름 필터와 재료 필터는 현재 Native Geometry Engine 출력에 연결됩니다. 기존 CAD_LAYER와 구형 Rule은 현재 출력 경로에 연결하지 않습니다.")),

        ("DWG 레이어 설정", new HelpTopic(
            "DWG 레이어 템플릿은 Revit 카테고리와 하위 카테고리의 투영·절단 형상을 어떤 CAD 레이어와 ACI 색상으로 보낼지 정합니다. " +
            "특정 유형 또는 복합벽·복합바닥의 특정 재료만 다른 레이어로 분류하는 필터도 같은 화면에서 관리합니다.",
            "1. 상단에서 템플릿을 선택하거나 [새로 만들기]·[복제]를 사용합니다.\r\n\r\n" +
            "2. 검색하고 카테고리 왼쪽의 +를 눌러 하위 항목을 펼칩니다.\r\n\r\n" +
            "3. 투영·절단 레이어, ACI 색상, 선종류와 선가중치를 지정합니다.\r\n\r\n" +
            "4. 유형 이름 필터는 대상 카테고리와 포함 문자열, 투영·절단 결과를 입력합니다.\r\n\r\n" +
            "5. 재료 필터는 프로젝트의 실제 Revit 재료를 선택하고 CAD 레이어와 색상을 지정합니다.\r\n\r\n" +
            "6. 저장 후 이 템플릿을 사용할 시트 또는 시트 세트에 연결합니다.",
            "[기본 카테고리 매핑]\r\n" +
            "선종류·선가중치의 ‘원본 유지’는 Revit 기본 DWG 표현을 유지한다는 뜻입니다. 창Export 템플릿은 Revit의 기존 DWG 출력 설정과 독립적이며 기존 설정을 자동으로 가져오지 않습니다. 템플릿은 파일로 내보내 다른 프로젝트에서 불러올 수 있고, 시트 세트가 사용 중인 템플릿은 삭제할 수 없습니다.\r\n\r\n" +
            "[유형 이름 필터]\r\n" +
            "선택 카테고리 안에서 Revit 유형 이름에 입력 문자열이 포함되는지 대소문자 구분 없이 비교합니다. 여러 필터가 동시에 맞으면 목록 위쪽의 첫 일치 필터가 적용되므로 구체적인 조건을 위에 둡니다.\r\n\r\n" +
            "[재료 필터]\r\n" +
            "선택한 실제 Revit 재료를 기준으로 호스트와 로드된 링크의 복합벽·복합바닥 재료층을 판정합니다. 임시 Part와 식별색은 출력 중에만 사용하고 원본 모델에 남기지 않습니다. 같은 복합객체의 공유 경계는 벽의 바깥→안쪽, 바닥의 위→아래 순서에서 더 안쪽·아래쪽 레이어가 한 번만 소유합니다.\r\n\r\n" +
            "[서로 붙인 단일 벽]\r\n" +
            "서로 다른 단일 벽이 정확히 같은 선에서 맞닿으면 구조 > 하지재 > 열/공기층 > 마감 2 > 마감 1 > 멤브레인층 순으로 경계 소유자를 하나만 정합니다. 기능이 같으면 어느 한쪽만 남지만 특정 객체가 항상 우선한다고 보장하지 않습니다. 실제 틈이나 겹침이 있으면 서로 다른 선이므로 양쪽이 모두 출력될 수 있습니다.\r\n\r\n" +
            "[필터가 예상과 다를 때]\r\n" +
            "대상 카테고리, 유형 문자열, 필터 순서, 실제 재료 연결, 복합구조 기능과 모델 간격을 확인합니다. 일부 선이 기본 레이어로 남으면 진단의 NativeOverlayRuleDiagnostics, SourceLayers, 일치·모호·분할 선 개수와 필터 경고를 확인합니다.")),

        ("시트 세트 구성", new HelpTopic(
            "시트 세트는 여러 Revit 시트를 하나의 최종 DWG로 묶는 출력 단위입니다. 호스트 모델과 로드된 로컬·네트워크 RVT 링크, 로드된 중첩 링크의 시트를 모델별 목록에서 선택할 수 있습니다.",
            "1. 왼쪽 모델 목록에서 호스트 또는 링크 모델을 펼칩니다.\r\n\r\n" +
            "2. 검색하거나 필요한 시트를 선택하고 새 세트를 만듭니다.\r\n\r\n" +
            "3. 세트 이름과 사용할 DWG 레이어 템플릿을 지정합니다.\r\n\r\n" +
            "4. 최종 배치 방향을 가로 또는 세로로 선택합니다.\r\n\r\n" +
            "5. 세트 안의 시트 순서를 위/아래 이동으로 정리합니다.\r\n\r\n" +
            "6. 저장한 뒤 [DWG 출력]에서 세트를 선택합니다.",
            "[시트 원본]\r\n" +
            "링크 시트는 원본 링크를 저장하지 않고 일회용 로컬 사본에서 처리한 뒤 닫고 정리합니다. 로드되지 않은 링크 또는 직접 복제할 수 없는 클라우드 링크는 출력할 수 없으며 원인을 표시합니다.\r\n\r\n" +
            "[템플릿 일관성]\r\n" +
            "한 세트 안의 모든 시트는 같은 레이어 템플릿을 사용해야 합니다. 시트가 이동·삭제되거나 링크가 언로드되었거나 템플릿이 없어지면 출력 전에 세트 구성을 다시 확인합니다.\r\n\r\n" +
            "[배치 방향과 순서]\r\n" +
            "가로는 첫 시트부터 좌→우, 세로는 위→아래 방향으로 배치합니다. 세트 구성원의 표시 순서가 최종 배치 순서이므로 번호 자동정렬이 필요한 경우 저장 전에 직접 순서를 확인합니다.\r\n\r\n" +
            "[세트 해제]\r\n" +
            "세트를 해제하면 구성 시트를 다시 개별 항목으로 돌립니다. 원본 Revit 시트를 삭제하는 기능이 아니며 창Export의 세트 구성만 변경합니다.")),

        ("DWG 출력", new HelpTopic(
            "DWG 출력은 선택한 시트를 Revit 기본 DWG로 만든 뒤 Native Geometry Engine으로 뷰 내용을 모형공간에 결합하고, 세트별 단일 DWG로 배치·저장합니다. 외부 CAD 프로그램은 설치하거나 실행하지 않습니다.",
            "1. 출력 폴더를 지정합니다. 기본 폴더가 적절한지 먼저 확인합니다.\r\n\r\n" +
            "2. 출력할 세트를 체크합니다. 비어 있거나 오류 표시가 있는 세트는 구성을 고칩니다.\r\n\r\n" +
            "3. 세트별 템플릿, 시트 수, 방향과 구성원 순서를 최종 확인합니다.\r\n\r\n" +
            "4. 출력을 실행하고 진행 중인 시트와 처리 단계를 확인합니다.\r\n\r\n" +
            "5. 결과 창에서 성공·실패·경고와 최종 경로를 확인합니다.\r\n\r\n" +
            "6. 최종 DWG를 열어 원본 시트와 비교하고 필요한 경우 진단 TXT를 저장합니다.",
            "[파일 형식과 축척]\r\n" +
            "최종 파일은 DWG 2010, 모형공간, 밀리미터 단위입니다. 시트별 가장 큰 2D 뷰의 축척을 기준으로 도곽까지 모형공간 mm로 확대하고 같은 시트의 다른 뷰는 상대 축척을 유지합니다. 저장 직후 다시 열어 파일 구조를 검사합니다.\r\n\r\n" +
            "[Native Geometry Engine]\r\n" +
            "Revit 기본 시트 DWG가 유일한 최종 형상 원본입니다. 임시 필터 DWG는 유형·재료 분류 근거로만 사용하며 정확히 일치하는 Native 선·폴리라인·해치에 레이어 정보를 전사합니다. 필터가 실패하면 기본 카테고리 출력은 보존하고 경고와 Manifest에 실패 내용을 남깁니다.\r\n\r\n" +
            "[패밀리와 뷰]\r\n" +
            "고정 형상의 로드 가능 패밀리와 Revit 상세 그룹은 가능한 경우 공유 DWG 블록으로 유지합니다. 구조 보·기둥, 커튼월·난간 구성요소, 내부 작성·선형·스케치·2레벨·적응형 패밀리는 개별 객체로 유지될 수 있습니다. 원근·음영 뷰는 생략될 수 있고 이미지는 사각형 경계로 대체됩니다.\r\n\r\n" +
            "[취소와 부분 실패]\r\n" +
            "취소하면 진행 중 세트의 최종 파일은 만들지 않으며 앞서 완료된 세트는 결과에 남습니다. 한 세트가 실패해도 가능한 다음 세트는 계속 처리하고 실패 단계와 상세 오류를 기록합니다.")),

        ("설정", new HelpTopic(
            "설정은 전역폭 폴리선으로 변환할 Revit 선 스타일의 판별 문자열과, 세트 안 시트 사이의 공통 배치 간격을 관리합니다. 두 값은 현재 프로젝트의 창Export 설정에 저장됩니다.",
            "1. 창Export 리본에서 톱니바퀴 아이콘의 [설정]을 누릅니다.\r\n\r\n" +
            "2. 전역폭이 필요한 경우 대상 Revit 선 스타일 이름에 포함된 판별 문자열을 입력합니다.\r\n\r\n" +
            "3. 전역폭을 사용하지 않으려면 판별 문자열을 비웁니다.\r\n\r\n" +
            "4. 모든 세트에 적용할 공통 시트 간격을 mm로 입력합니다.\r\n\r\n" +
            "5. [설정 저장]을 눌러 현재 프로젝트에 저장합니다.",
            "[전역폭]\r\n" +
            "Revit 선 스타일 이름에 판별 문자열이 포함되면 해당 선을 전역폭 폴리선으로 변환합니다. 폭은 Revit 기본 DWG의 선가중치와 시트 축척으로 계산합니다. 실제로 변환된 선만 선 스타일 RGB를 유지하며, 빈 문자열이면 변환하지 않습니다.\r\n\r\n" +
            "[공통 간격]\r\n" +
            "가로·세로 배치에 모두 적용되며 축척 변환 후 모형공간 mm 기준입니다. 0 mm는 시트 바깥 경계를 붙이는 값이지 도곽 안쪽 여백을 제거하는 값이 아닙니다.\r\n\r\n" +
            "[적용 시점]\r\n" +
            "설정 변경은 다음 출력부터 적용됩니다. 이미 생성된 DWG를 자동으로 다시 만들거나 수정하지 않습니다. 출력 설정값과 최종 DWG 반영 상태는 별개이므로 실제 출력 결과와 Manifest를 확인합니다.")),

        ("기술 진단", new HelpTopic(
            "기술 진단은 현재 애드인·Revit·프로젝트와 DWG 출력 준비 상태를 빠르게 요약하는 읽기 전용 기능입니다. 실제 출력의 상세 증거는 결과 창의 Manifest JSON과 사용자가 저장하는 진단 TXT에 기록됩니다.",
            "1. 문제가 발생한 프로젝트와 시트를 연 상태에서 [기술 진단]을 누릅니다.\r\n\r\n" +
            "2. 애드인·Revit 버전, 모델, 출력 설정 수, 레이어 매핑 수와 시트 수를 확인합니다.\r\n\r\n" +
            "3. 실제 DWG 출력을 한 번 실행하고 결과 창의 경고를 확인합니다.\r\n\r\n" +
            "4. [진단 TXT 저장]을 눌러 ChangExport_Diagnostics_*.txt를 저장합니다.\r\n\r\n" +
            "5. 문제 화면, 기대한 레이어 이름, 호스트/링크 여부와 모델링 조건을 진단 파일과 함께 전달합니다.",
            "[기술 진단 창]\r\n" +
            "현재 버전, Revit 버전, 모델명, 창Export 출력 설정과 매핑 수, 시트 수, 내장 DWG 엔진과 설정 파일 경로를 표시합니다. 이 요약만으로 실제 DWG 형상 품질을 판정할 수는 없습니다.\r\n\r\n" +
            "[Manifest JSON]\r\n" +
            "출력할 때마다 출력 폴더에 ChangExport_Manifest_날짜_작업ID.json을 만듭니다. 템플릿, 필터 수, 세트 구성, 시트 원본, 경고, 처리 시간과 형상 엔진 통계를 기록합니다. filterMatches는 Revit에서 조건에 맞은 요소 수이고 CustomRuleEntityCounts와 NativeOverlay 값은 최종 DWG 형상 판정 결과이므로 의미가 다릅니다.\r\n\r\n" +
            "[진단 TXT]\r\n" +
            "성공·실패, 세트별 메시지, 경고, 오류 상세, 시트 진단과 처리 시간을 읽기 쉬운 텍스트로 저장합니다. 재료·유형 필터 문제에는 실제 재료명, 복합벽/복합바닥/단일 벽 여부, 링크 여부와 기대 레이어를 함께 알려야 원인을 좁힐 수 있습니다.\r\n\r\n" +
            "[경고 해석]\r\n" +
            "‘필터 미반영’은 기본 카테고리 DWG는 만들었지만 필터를 적용하지 못했다는 뜻입니다. ‘도면 내용 확인’은 저장됐지만 원본과 내용 비교가 필요하다는 뜻입니다. ‘일치하지 않음/모호함’은 임시 분류 형상과 Native 형상을 안전하게 하나로 확정하지 못해 원본 레이어를 유지했을 수 있다는 뜻입니다."))
    ];

    private static TabPage CreateHelpTab(string title, HelpTopic topic)
    {
        var tab = new TabPage(title) { Padding = new Padding(16), BackColor = Color.FromArgb(245, 247, 250) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = tab.BackColor
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 246));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateSectionTitle("기능 개요"), 0, 0);
        layout.Controls.Add(CreateTextBox(topic.Overview), 0, 1);
        layout.Controls.Add(CreateSectionTitle("기본 사용 방법"), 0, 2);
        layout.Controls.Add(CreateTextBox(topic.Steps), 0, 3);
        layout.Controls.Add(CreateSectionTitle("상세 동작 원리 및 설정"), 0, 4);
        layout.Controls.Add(CreateTextBox(topic.Details), 0, 5);
        tab.Controls.Add(layout);
        return tab;
    }

    private static Label CreateSectionTitle(string text) => new()
    {
        AutoSize = true,
        Margin = new Padding(2, 4, 2, 8),
        Font = new Font("맑은 고딕", 10.5F, FontStyle.Bold),
        ForeColor = UiTheme.Navy,
        Text = text
    };

    private static RichTextBox CreateTextBox(string text) => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        ForeColor = Color.FromArgb(38, 50, 66),
        DetectUrls = false,
        Font = new Font("맑은 고딕", 9.5F),
        Text = text,
        Margin = new Padding(0, 0, 0, 14),
        ScrollBars = RichTextBoxScrollBars.Vertical
    };
}
