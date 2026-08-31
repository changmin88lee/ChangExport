using System.Drawing;
using System.Windows.Forms;
using ChangExport.Export;

namespace ChangExport.UI;

public sealed class ExportResultForm : Form
{
    public ExportResultForm(ExportRunResult result)
    {
        Text = "DWG 출력 결과";
        Width = 820;
        Height = 540;
        UiTheme.Apply(this);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(UiTheme.Heading(result.Cancelled ? "DWG 출력 취소" : result.FailedCount == 0 ? "모형공간 DWG 출력 완료" : "DWG 출력 확인 필요"));
        root.Controls.Add(UiTheme.Muted($"성공 {result.SuccessCount} · 실패 {result.FailedCount} · Manifest: {result.ManifestPath}"));
        var list = new ListBox { Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        foreach (ExportItemResult item in result.Items)
        {
            list.Items.Add($"[{(item.Success ? "성공" : "실패")}] {item.SheetNumber} {item.SheetName}  {item.Message}");
            foreach (string warning in item.Warnings.Distinct()) list.Items.Add("  확인 필요: " + warning);
        }
        list.Items.Add("원본 시트와 도곽·치수·글자·해치·축척을 비교하세요. 시트 지면 mm 기준이며 실물 1:1 변환은 아닙니다.");
        list.Items.Add("임시 DWG/진단 파일: " + result.WorkFolder);
        root.Controls.Add(list);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        Button close = UiTheme.PrimaryButton("닫기");
        close.Click += (_, _) => Close();
        Button open = UiTheme.SecondaryButton("출력 폴더 열기");
        open.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", result.OutputFolder) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "폴더 열기 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        actions.Controls.Add(close);
        actions.Controls.Add(open);
        root.Controls.Add(actions);
        AcceptButton = close;
    }
}
