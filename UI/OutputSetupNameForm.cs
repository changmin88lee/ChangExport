using System.Drawing;
using System.Windows.Forms;

namespace ChangExport.UI;

internal sealed class OutputSetupNameForm : Form
{
    private readonly TextBox _name;
    public string SetupName => _name.Text.Trim();
    public OutputSetupNameForm(string title, string name)
    {
        Text = title; ClientSize = new Size(420, 155); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 }; Controls.Add(root);
        root.Controls.Add(new Label { Text = "출력 설정 이름", AutoSize = true });
        _name = new TextBox { Text = name, Dock = DockStyle.Fill }; root.Controls.Add(_name);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var save = UiTheme.PrimaryButton("확인"); save.Click += (_, _) => { if (SetupName.Length == 0) return; DialogResult = DialogResult.OK; Close(); };
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save); actions.Controls.Add(cancel); root.Controls.Add(actions); AcceptButton = save; CancelButton = cancel;
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}
