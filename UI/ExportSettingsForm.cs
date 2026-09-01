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
    public string Template { get; init; } = string.Empty;
}

public sealed class ExportSettingsForm : Form
{
    private readonly IReadOnlyList<SheetDescriptor> _sheets;
    private readonly IReadOnlyList<LayerTemplateChoice> _templates;
    private readonly Action<IReadOnlyList<SheetSetDefinition>, IReadOnlyDictionary<string, string>> _saveSets;
    private Dictionary<string, string> _assignments;
    private readonly DataGridView _grid;
    private BindingList<ExportSetChoice> _choices = new();
    private readonly TextBox _folder;
    public string OutputFolder => _folder.Text.Trim();
    public IReadOnlyList<SheetSetDefinition> SelectedSets => _choices.Where(s => s.Selected).Select(s => s.Set.Copy()).ToList();
    public ExportSettingsForm(IReadOnlyList<SheetDescriptor> sheets, IReadOnlyList<SheetSetDefinition> sets,
        IReadOnlyList<string> setups, string selectedSetup, string defaultFolder, Action<IReadOnlyList<SheetSetDefinition>> saveSets)
        : this(sheets, sets, setups.Select((name, index) => new LayerTemplateChoice(name, name)).ToList(),
            sets.SelectMany(s => s.SheetUniqueIds.Select(id => (id, s.TemplateId))).Where(x => x.TemplateId.Length > 0).ToDictionary(x => x.id, x => x.TemplateId),
            defaultFolder, (result, _) => saveSets(result)) { }

    public ExportSettingsForm(IReadOnlyList<SheetDescriptor> sheets, IReadOnlyList<SheetSetDefinition> sets,
        IReadOnlyList<LayerTemplateChoice> templates, IReadOnlyDictionary<string, string> assignments,
        string defaultFolder, Action<IReadOnlyList<SheetSetDefinition>, IReadOnlyDictionary<string, string>> saveSets)
    {
        _sheets = sheets; _templates = templates; _saveSets = saveSets;
        _assignments = new Dictionary<string, string>(assignments, StringComparer.Ordinal);
        Text = "모형공간 DWG 출력"; ClientSize = new Size(1050, 700); MinimumSize = new Size(900, 590); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("세트별 DWG · 모형공간 출력"));
        title.Controls.Add(UiTheme.Muted("Revit 독립 실행 · 내장 DWG 엔진으로 시트의 도곽/뷰/주석을 모형공간에 배치합니다.")); root.Controls.Add(title);
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 14, 0, 8) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.Controls.Add(new Label { Text = "출력 폴더", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _folder = new TextBox { Dock = DockStyle.Fill, Text = defaultFolder }; settings.Controls.Add(_folder, 1, 0);
        var side = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var browse = UiTheme.SecondaryButton("찾아보기"); browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = OutputFolder }; if (picker.ShowDialog(this) == DialogResult.OK) _folder.Text = picker.SelectedPath; };
        var configure = UiTheme.SecondaryButton("시트·템플릿·세트 구성"); configure.Click += (_, _) => ConfigureSets();
        side.Controls.Add(browse); side.Controls.Add(configure); settings.Controls.Add(side, 2, 0); root.Controls.Add(settings);
        var notice = UiTheme.Muted("DWG 2010 · 모형공간 mm · 시트별 축척 적용 · 전역폭·간격 변경: 창Export 탭 → 설정\n원근·음영 뷰는 생략하며 이미지는 사각형으로 대체합니다.");
        notice.MaximumSize = new Size(980, 0); notice.Margin = new Padding(0, 0, 0, 12); root.Controls.Add(notice);
        _grid = UiTheme.Grid(); _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(ExportSetChoice.Selected), HeaderText = "출력", FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Name), HeaderText = "세트 이름", ReadOnly = true, FillWeight = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Template), HeaderText = "DWG 레이어 템플릿", ReadOnly = true, FillWeight = 100 });
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
            Template = s.TemplateId.Length == 0 ? "미지정" : _templates.FirstOrDefault(t => t.Id == s.TemplateId)?.ToString() ?? "없는 템플릿",
            Members = string.Join(" → ", s.SheetUniqueIds.Select(id => names.GetValueOrDefault(id, "[없는 시트]"))) }).ToList()); _grid.DataSource = _choices;
    }
    private void ConfigureSets()
    {
        using var dialog = new SheetGroupManagerForm(_sheets, _choices.Select(c => c.Set), _templates, _assignments);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _assignments = new Dictionary<string, string>(dialog.ResultAssignments, StringComparer.Ordinal);
            _saveSets(dialog.ResultSets, _assignments); Bind(dialog.ResultSets);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "세트 저장 실패"); }
    }
    private void Execute()
    {
        _grid.EndEdit();
        if (SelectedSets.Count == 0) { MessageBox.Show(this, "출력할 세트를 선택하세요."); return; }
        if (string.IsNullOrWhiteSpace(OutputFolder)) { MessageBox.Show(this, "출력 폴더를 지정하세요."); return; }
        if (SelectedSets.Any(s => s.SheetUniqueIds.Count == 0 || s.SheetUniqueIds.Any(id => !_sheets.Any(sheet => sheet.UniqueId == id))))
        { MessageBox.Show(this, "빈 세트 또는 프로젝트에 없는 시트가 있습니다. 시트 세트 구성에서 확인하세요."); return; }
        if (SelectedSets.Any(s => string.IsNullOrWhiteSpace(s.TemplateId) || !_templates.Any(t => t.Id == s.TemplateId)))
        { MessageBox.Show(this, "DWG 레이어 템플릿이 미지정이거나 삭제된 세트가 있습니다. 시트·템플릿·세트 구성에서 확인하세요."); return; }
        if (SelectedSets.Any(s => s.SheetUniqueIds.Any(id => _assignments.GetValueOrDefault(id, string.Empty) != s.TemplateId)))
        { MessageBox.Show(this, "세트 안에 서로 다른 DWG 레이어 템플릿 배정이 있습니다. 시트·템플릿·세트 구성에서 다시 지정하세요."); return; }
        DialogResult = DialogResult.OK; Close();
    }
}
