using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace ChangExport.UI;

public sealed class ExportSheetChoice
{
    public bool Selected { get; set; } = true;
    public long ElementId { get; init; }
    public string Group { get; init; } = string.Empty;
    public int Order { get; init; }
    public string SheetNumber { get; init; } = string.Empty;
    public string SheetName { get; init; } = string.Empty;
}

public sealed class ExportSettingsForm : Form
{
    private readonly BindingList<ExportSheetChoice> _sheets;
    private readonly ComboBox _setupBox;
    private readonly TextBox _folderBox;
    private readonly ComboBox _arrangeBox;
    private readonly NumericUpDown _columnsBox;
    private readonly NumericUpDown _marginBox;
    private readonly DataGridView _grid;

    public string SelectedSetup => _setupBox.SelectedItem?.ToString() ?? string.Empty;
    public string OutputFolder => _folderBox.Text.Trim();
    public string ArrangeMode => _arrangeBox.SelectedItem?.ToString() ?? "가로 일렬";
    public int Columns => (int)_columnsBox.Value;
    public double MarginMm => (double)_marginBox.Value;
    public IReadOnlyList<ExportSheetChoice> SelectedSheets => _sheets.Where(x => x.Selected).ToList();

    public ExportSettingsForm(
        IEnumerable<ExportSheetChoice> sheets,
        IEnumerable<string> setupNames,
        string profileName,
        string defaultFolder,
        string classificationSummary)
    {
        _sheets = new BindingList<ExportSheetChoice>(sheets.OrderBy(x => x.Group).ThenBy(x => x.Order).ThenBy(x => x.SheetNumber).ToList());
        Text = "회사 DWG 출력";
        Width = 1020;
        Height = 710;
        MinimumSize = new Size(840, 590);
        UiTheme.Apply(this);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("회사 DWG 출력"));
        title.Controls.Add(UiTheme.Muted($"Profile: {profileName} · {classificationSummary}"));
        root.Controls.Add(title);

        var settings = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, Margin = new Padding(0, 14, 0, 6) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        settings.Controls.Add(new Label { Text = "Export Setup", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _setupBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _setupBox.Items.Add("(Revit 기본값)");
        foreach (string name in setupNames) _setupBox.Items.Add(name);
        _setupBox.SelectedIndex = 0;
        settings.Controls.Add(_setupBox, 1, 0);
        settings.Controls.Add(new Label { Text = "배치 방식", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 4, 0) }, 2, 0);
        _arrangeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _arrangeBox.Items.AddRange(new object[] { "가로 일렬", "세로 일렬", "N열 격자", "자동 격자" });
        _arrangeBox.SelectedIndex = 0;
        settings.Controls.Add(_arrangeBox, 3, 0);
        settings.Controls.Add(new Label { Text = "열 수", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 4, 0) }, 4, 0);
        _columnsBox = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 5, Dock = DockStyle.Fill };
        settings.Controls.Add(_columnsBox, 5, 0);

        settings.Controls.Add(new Label { Text = "출력 폴더", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _folderBox = new TextBox { Text = defaultFolder, Dock = DockStyle.Fill };
        settings.Controls.Add(_folderBox, 1, 1);
        Button browse = UiTheme.SecondaryButton("찾아보기");
        browse.Click += (_, _) => BrowseFolder();
        settings.Controls.Add(browse, 2, 1);
        settings.Controls.Add(new Label { Text = "Margin (mm)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 0, 4, 0) }, 4, 1);
        _marginBox = new NumericUpDown { Minimum = 0, Maximum = 100000, DecimalPlaces = 0, Value = 10000, Increment = 1000, Dock = DockStyle.Fill };
        settings.Controls.Add(_marginBox, 5, 1);
        root.Controls.Add(settings);

        var notice = new Label
        {
            Text = "Beta 범위: Revit Native DWG를 Sheet별로 그룹 폴더에 안전하게 출력합니다. 단일 DWG 병합·Model Space 평면화·최종 Layer Remap은 DWG SDK 연결 후 활성화됩니다.",
            AutoSize = true,
            ForeColor = Color.FromArgb(148, 89, 20),
            Padding = new Padding(0, 4, 0, 8)
        };
        root.Controls.Add(notice);

        _grid = UiTheme.Grid();
        _grid.AutoGenerateColumns = false;
        _grid.DataSource = _sheets;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(ExportSheetChoice.Selected), HeaderText = "출력", FillWeight = 38 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSheetChoice.Group), HeaderText = "Group", ReadOnly = true, FillWeight = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSheetChoice.Order), HeaderText = "Order", ReadOnly = true, FillWeight = 45 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSheetChoice.SheetNumber), HeaderText = "Sheet 번호", ReadOnly = true, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSheetChoice.SheetName), HeaderText = "Sheet 이름", ReadOnly = true, FillWeight = 150 });
        root.Controls.Add(_grid);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        Button run = UiTheme.PrimaryButton("Native DWG 출력 실행");
        run.Click += (_, _) => ValidateAndClose();
        Button cancel = UiTheme.SecondaryButton("취소");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Button all = UiTheme.SecondaryButton("전체 선택");
        all.Click += (_, _) => { foreach (ExportSheetChoice row in _sheets) row.Selected = true; _grid.Refresh(); };
        Button none = UiTheme.SecondaryButton("전체 해제");
        none.Click += (_, _) => { foreach (ExportSheetChoice row in _sheets) row.Selected = false; _grid.Refresh(); };
        actions.Controls.Add(run);
        actions.Controls.Add(cancel);
        actions.Controls.Add(none);
        actions.Controls.Add(all);
        root.Controls.Add(actions);
        CancelButton = cancel;
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "창Export DWG 출력 폴더", SelectedPath = _folderBox.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dialog.SelectedPath;
    }

    private void ValidateAndClose()
    {
        _grid.EndEdit();
        if (_sheets.All(x => !x.Selected))
        {
            MessageBox.Show(this, "출력할 Sheet를 하나 이상 선택하세요.", "창Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            MessageBox.Show(this, "출력 폴더를 지정하세요.", "창Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
