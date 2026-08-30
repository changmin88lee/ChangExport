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

        root.Controls.Add(UiTheme.Heading(result.FailedCount == 0 ? "Native DWG 출력 완료" : "DWG 출력 확인 필요"));
        root.Controls.Add(UiTheme.Muted($"성공 {result.SuccessCount} · 실패 {result.FailedCount} · Manifest: {result.ManifestPath}"));
        var list = new ListBox { Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        foreach (ExportItemResult item in result.Items)
            list.Items.Add($"[{(item.Success ? "성공" : "실패")}] {item.SheetNumber} {item.SheetName}  {item.Message}");
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
