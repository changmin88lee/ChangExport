using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;

namespace ChangExport.UI;

public sealed class ExportSetChoice
{
    public bool Selected { get; set; } = true;
    public SheetSetDefinition Set { get; init; } = new();
    public string Name => Set.Name;
    public int Count => Set.SheetUniqueIds.Count;
    public string Direction => Set.Direction == "Vertical" ? "세로 일렬" : "가로 일렬";
    public double Margin => Set.MarginMm;
    public string Members { get; init; } = string.Empty;
}

public sealed class ExportSettingsForm : Form
{
    private readonly IReadOnlyList<SheetDescriptor> _sheets;
    private readonly Action<IReadOnlyList<SheetSetDefinition>> _saveSets;
    private readonly DataGridView _grid;
    private BindingList<ExportSetChoice> _choices = new();
    private readonly ComboBox _setup;
    private readonly TextBox _folder;
    public string SelectedSetup => (_setup.SelectedItem as SetupItem)?.Name ?? string.Empty;
    public string OutputFolder => _folder.Text.Trim();
    public IReadOnlyList<SheetSetDefinition> SelectedSets => _choices.Where(s => s.Selected).Select(s => s.Set.Copy()).ToList();
    private sealed record SetupItem(string Name) { public override string ToString() => Name.Length == 0 ? "Revit 기본값" : Name; }

    public ExportSettingsForm(IReadOnlyList<SheetDescriptor> sheets, IReadOnlyList<SheetSetDefinition> sets,
        IReadOnlyList<string> setups, string selectedSetup, string defaultFolder, Action<IReadOnlyList<SheetSetDefinition>> saveSets)
    {
        _sheets = sheets; _saveSets = saveSets;
        Text = "모형공간 DWG 출력"; ClientSize = new Size(1050, 700); MinimumSize = new Size(900, 590); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("세트별 DWG · 모형공간 출력"));
        title.Controls.Add(UiTheme.Muted("AutoCAD 2023 연동 · 도곽/뷰/주석의 시트 배치를 모형공간으로 옮겨 세트별로 묶습니다.")); root.Controls.Add(title);
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 14, 0, 8) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.Controls.Add(new Label { Text = "Revit 출력 설정", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _setup = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string setup in setups) _setup.Items.Add(new SetupItem(setup));
        _setup.SelectedItem = _setup.Items.Cast<SetupItem>().FirstOrDefault(s => s.Name == selectedSetup) ?? _setup.Items[0];
        settings.Controls.Add(_setup, 1, 0);
        var configure = UiTheme.SecondaryButton("시트 세트 구성"); configure.Click += (_, _) => ConfigureSets(); settings.Controls.Add(configure, 2, 0);
        settings.Controls.Add(new Label { Text = "출력 폴더", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _folder = new TextBox { Dock = DockStyle.Fill, Text = defaultFolder }; settings.Controls.Add(_folder, 1, 1);
        var browse = UiTheme.SecondaryButton("찾아보기"); browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = OutputFolder }; if (picker.ShowDialog(this) == DialogResult.OK) _folder.Text = picker.SelectedPath; }; settings.Controls.Add(browse, 2, 1); root.Controls.Add(settings);
        var notice = UiTheme.Muted("출력 기준: 시트 지면 크기(mm), 시트의 상대 축척 유지. 각 뷰의 실물 1:1 변환은 하지 않습니다.\n변환 과정에서 잘린 치수·블록·해치 등의 표현이 달라질 수 있으므로 결과를 확인하세요. 실패한 세트는 최종 DWG로 저장하지 않습니다.");
        notice.MaximumSize = new Size(980, 0); notice.Margin = new Padding(0, 0, 0, 12); root.Controls.Add(notice);
        _grid = UiTheme.Grid(); _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(ExportSetChoice.Selected), HeaderText = "출력", FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Name), HeaderText = "세트 이름", ReadOnly = true, FillWeight = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Count), HeaderText = "시트 수", ReadOnly = true, FillWeight = 45 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Direction), HeaderText = "배치 방향", ReadOnly = true, FillWeight = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Margin), HeaderText = "간격 mm", ReadOnly = true, FillWeight = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Members), HeaderText = "시트 순서", ReadOnly = true, FillWeight = 185 }); root.Controls.Add(_grid);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        var run = UiTheme.PrimaryButton("모형공간 DWG 출력"); run.Click += (_, _) => Execute();
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        var all = UiTheme.SecondaryButton("전체 선택"); all.Click += (_, _) => { foreach (var c in _choices) c.Selected = true; _grid.Refresh(); };
        var none = UiTheme.SecondaryButton("전체 해제"); none.Click += (_, _) => { foreach (var c in _choices) c.Selected = false; _grid.Refresh(); };
        actions.Controls.Add(run); actions.Controls.Add(cancel); actions.Controls.Add(none); actions.Controls.Add(all); root.Controls.Add(actions); CancelButton = cancel;
        Bind(sets);
    }
    private void Bind(IEnumerable<SheetSetDefinition> sets)
    {
        var names = _sheets.ToDictionary(s => s.UniqueId, s => s.Number);
        _choices = new BindingList<ExportSetChoice>(sets.Select(s => new ExportSetChoice { Set = s.Copy(),
            Members = string.Join(" → ", s.SheetUniqueIds.Select(id => names.GetValueOrDefault(id, "[없는 시트]"))) }).ToList()); _grid.DataSource = _choices;
    }
    private void ConfigureSets()
    {
        using var dialog = new SheetGroupManagerForm(_sheets, _choices.Select(c => c.Set));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _saveSets(dialog.ResultSets); Bind(dialog.ResultSets); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "세트 저장 실패"); }
    }
    private void Execute()
    {
        _grid.EndEdit();
        if (SelectedSets.Count == 0) { MessageBox.Show(this, "출력할 세트를 선택하세요."); return; }
        if (string.IsNullOrWhiteSpace(OutputFolder)) { MessageBox.Show(this, "출력 폴더를 지정하세요."); return; }
        if (SelectedSets.Any(s => s.SheetUniqueIds.Count == 0 || s.SheetUniqueIds.Any(id => !_sheets.Any(sheet => sheet.UniqueId == id))))
        { MessageBox.Show(this, "빈 세트 또는 프로젝트에 없는 시트가 있습니다. 시트 세트 구성에서 확인하세요."); return; }
        DialogResult = DialogResult.OK; Close();
    }
}
