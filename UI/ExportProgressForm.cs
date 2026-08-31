using System.Drawing;
using System.Windows.Forms;
using ChangExport.Export;

namespace ChangExport.UI;

public sealed class ExportProgressForm : Form
{
    private readonly Label _status;
    private bool _cancel, _finished;
    public ExportRunResult? Result { get; private set; }
    public Exception? Failure { get; private set; }
    public ExportProgressForm(Func<Action<string>, Func<bool>, Action, ExportRunResult> work)
    {
        Text = "모형공간 DWG 출력 중"; ClientSize = new Size(650, 190); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 3 };
        _status = new Label { Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(610, 0), Text = "출력을 준비합니다." }; root.Controls.Add(_status);
        root.Controls.Add(new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Height = 20 });
        var cancel = UiTheme.SecondaryButton("출력 취소"); cancel.Click += (_, _) => { _cancel = true; cancel.Enabled = false; _status.Text = "취소 중입니다. 현재 Revit Export가 끝나면 중단합니다."; }; root.Controls.Add(cancel); Controls.Add(root);
        FormClosing += (_, e) => { if (!_finished) { _cancel = true; e.Cancel = true; } };
        Shown += (_, _) => BeginInvoke(new Action(() =>
        {
            try { Result = work(text => { _status.Text = text; System.Windows.Forms.Application.DoEvents(); }, () => _cancel, System.Windows.Forms.Application.DoEvents); }
            catch (Exception ex) { Failure = ex; }
            finally { _finished = true; Close(); }
        }));
    }
}
