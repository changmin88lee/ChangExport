using System.Drawing;
using System.Text;
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

        root.Controls.Add(UiTheme.Heading(result.Cancelled ? "DWG 출력 취소" : result.FailedCount == 0 && result.Items.All(i => i.Warnings.Count == 0) ? "모형공간 DWG 저장 완료" : "DWG 출력 확인 필요"));
        root.Controls.Add(UiTheme.Muted($"저장 {result.SuccessCount} · 실패 {result.FailedCount} · 안내가 있는 세트 {result.Items.Count(i => i.Warnings.Count > 0)} · Manifest: {result.ManifestPath}"));
        var list = new ListBox { Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0), HorizontalScrollbar = true };
        foreach (ExportItemResult item in result.Items)
        {
            string[] messageLines = item.Message.Replace("\r\n", "\n").Split('\n');
            list.Items.Add($"[{(item.Success ? item.Warnings.Count > 0 ? "저장 완료 · 안내 확인" : "성공" : "실패")}] {item.SheetNumber} {item.SheetName}  {messageLines[0]}");
            foreach (string line in messageLines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line))) list.Items.Add("  진단: " + line.Trim());
            foreach (string warning in item.Warnings.Distinct()) list.Items.Add("  확인 필요: " + warning);
            if (!string.IsNullOrWhiteSpace(item.ErrorDetails))
                foreach (string line in item.ErrorDetails.Replace("\r\n", "\n").Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line)).Distinct())
                    list.Items.Add("  오류 상세: " + line.Trim());
        }
        list.Items.Add("원본 시트와 도곽·치수·글자·해치를 비교하세요. 시트별 기준 뷰는 실물 크기(mm), 다른 축척 뷰는 상대 크기를 유지합니다.");
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
        Button saveDiagnostics = UiTheme.SecondaryButton("진단 TXT 저장");
        saveDiagnostics.Click += (_, _) =>
        {
            string manifestName = Path.GetFileNameWithoutExtension(result.ManifestPath);
            string suggested = string.IsNullOrWhiteSpace(manifestName) ? "ChangExport_진단" : manifestName.Replace("Manifest", "Diagnostics", StringComparison.OrdinalIgnoreCase);
            using var picker = new SaveFileDialog { Filter = "텍스트 파일 (*.txt)|*.txt", AddExtension = true, DefaultExt = "txt",
                FileName = suggested + ".txt", InitialDirectory = Directory.Exists(result.OutputFolder) ? result.OutputFolder : string.Empty, OverwritePrompt = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(picker.FileName, ExportDiagnosticText.Build(result), new UTF8Encoding(true));
                MessageBox.Show(this, "진단 TXT를 저장했습니다.\n" + picker.FileName, "진단 저장 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "진단 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        actions.Controls.Add(close);
        actions.Controls.Add(saveDiagnostics);
        actions.Controls.Add(open);
        root.Controls.Add(actions);
        AcceptButton = close;
    }
}
