using System.Drawing;
using System.Windows.Forms;

namespace ChangExport.UI;

public sealed class AciColorDialog : Form
{
    private readonly Panel _preview;
    private readonly Label _number;
    public int SelectedIndex { get; private set; }
    public AciColorDialog(int current)
    {
        Text = "CAD 색상 선택 · ACI";
        ClientSize = new Size(660, 430); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root); root.Controls.Add(UiTheme.Heading("색상을 선택하세요"));
        var palette = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 24, RowCount = 10, Margin = new Padding(0, 12, 0, 10) };
        for (int column = 0; column < 24; column++) palette.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 24));
        for (int row = 0; row < 10; row++) palette.RowStyles.Add(new RowStyle(SizeType.Percent, 10));
        for (int hue = 0; hue < 24; hue++) for (int shade = 0; shade < 10; shade++) palette.Controls.Add(Swatch(10 + hue * 10 + shade), hue, shade);
        root.Controls.Add(palette);
        var basics = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        foreach (int i in Enumerable.Range(1, 9).Concat(Enumerable.Range(250, 6)))
        { Button button = Swatch(i); button.Dock = DockStyle.None; button.Size = new Size(32, 28); basics.Controls.Add(button); }
        root.Controls.Add(basics);
        var selection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 6) };
        _preview = new Panel { Size = new Size(70, 28), BorderStyle = BorderStyle.FixedSingle };
        _number = new Label { AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
        selection.Controls.Add(_preview); selection.Controls.Add(_number); root.Controls.Add(selection);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = UiTheme.PrimaryButton("선택"); ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(ok); actions.Controls.Add(cancel); root.Controls.Add(actions);
        AcceptButton = ok; CancelButton = cancel; SelectColor(current is >= 1 and <= 255 ? current : 7);
    }
    private Button Swatch(int index)
    {
        var button = new Button { Dock = DockStyle.Fill, Margin = new Padding(1), FlatStyle = FlatStyle.Flat,
            BackColor = AciPalette.GetColor(index), UseVisualStyleBackColor = false, AccessibleName = $"ACI {index}", Tag = index };
        button.FlatAppearance.BorderColor = Color.FromArgb(175, 180, 187);
        button.Click += (_, _) => SelectColor(index);
        button.MouseEnter += (_, _) => _number.Text = $"ACI {index}  ·  선택된 색상 {SelectedIndex}";
        button.MouseLeave += (_, _) => _number.Text = $"선택된 색상 번호: {SelectedIndex}";
        return button;
    }
    private void SelectColor(int index)
    { SelectedIndex = index; _preview.BackColor = AciPalette.GetColor(index); _number.Text = $"선택된 색상 번호: {index}"; }
}
