using System.Drawing;
using System.Windows.Forms;

namespace ChangExport.UI;

internal static class UiTheme
{
    public static readonly Color Navy = Color.FromArgb(32, 48, 71);
    public static readonly Color Blue = Color.FromArgb(42, 111, 184);
    public static readonly Color Background = Color.FromArgb(245, 247, 250);
    public static readonly Color Border = Color.FromArgb(214, 219, 226);

    public static void Apply(Form form)
    {
        form.Font = new Font("맑은 고딕", 9F);
        form.BackColor = Background;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.MinimizeBox = false;
        form.ShowIcon = false;
    }

    public static Button PrimaryButton(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(96, 34),
            BackColor = Blue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(12, 2, 12, 2)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public static Button SecondaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(88, 34),
        BackColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Padding = new Padding(10, 2, 10, 2)
    };

    public static DataGridView Grid() => new()
    {
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        Dock = DockStyle.Fill
    };

    public static Label Heading(string text) => new()
    {
        Text = text,
        Font = new Font("맑은 고딕", 15F, FontStyle.Bold),
        ForeColor = Navy,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 4)
    };

    public static Label Muted(string text) => new()
    {
        Text = text,
        ForeColor = Color.FromArgb(95, 103, 115),
        AutoSize = true
    };
}
