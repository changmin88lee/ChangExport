using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;
using ChangExport.Standards;

namespace ChangExport.UI;

public sealed class LayerRuleManagerForm : Form
{
    private readonly CadStandardRepository _repository;
    private readonly CadStandardProfile _profile;
    private readonly BindingList<CadLayerDefinition> _layers;
    private readonly BindingList<DwgRule> _rules;
    private readonly DataGridView _layerGrid;
    private readonly DataGridView _ruleGrid;
    private readonly Label _status;

    public LayerRuleManagerForm(CadStandardRepository repository, CadStandardProfile profile)
    {
        _repository = repository;
        _profile = profile;
        _layers = new BindingList<CadLayerDefinition>(profile.Layers);
        _rules = new BindingList<DwgRule>(profile.Rules);

        Text = "Layer / Rule 관리";
        Width = 1050;
        Height = 680;
        MinimumSize = new Size(860, 560);
        UiTheme.Apply(this);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("회사 CAD Standard"));
        title.Controls.Add(UiTheme.Muted($"Profile: {_profile.ProfileName} · Schema {_profile.SchemaVersion} · Rule {_profile.RuleSetVersion}"));
        root.Controls.Add(title);

        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        root.Controls.Add(tabs);
        _layerGrid = CreateLayerGrid();
        _ruleGrid = CreateRuleGrid();
        tabs.TabPages.Add(CreateTab("Layer 목록", _layerGrid, AddLayer, DeleteSelectedLayer));
        tabs.TabPages.Add(CreateTab("자동 분류 Rule", _ruleGrid, AddRule, DeleteSelectedRule));

        _status = UiTheme.Muted("Layer 이름·중복·색상과 Rule Target을 저장 전에 검사합니다.");
        _status.Margin = new Padding(0, 10, 0, 0);
        root.Controls.Add(_status);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        Button save = UiTheme.PrimaryButton("검사 후 저장");
        save.Click += (_, _) => SaveProfile();
        Button close = UiTheme.SecondaryButton("닫기");
        close.Click += (_, _) => Close();
        actions.Controls.Add(save);
        actions.Controls.Add(close);
        root.Controls.Add(actions);
    }

    private DataGridView CreateLayerGrid()
    {
        DataGridView grid = UiTheme.Grid();
        grid.AutoGenerateColumns = false;
        grid.DataSource = _layers;
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CadLayerDefinition.Name), HeaderText = "Layer 이름", FillWeight = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CadLayerDefinition.ColorIndex), HeaderText = "ACI 색상", FillWeight = 65 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CadLayerDefinition.Linetype), HeaderText = "선종류", FillWeight = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CadLayerDefinition.LineweightMm), HeaderText = "선가중치", FillWeight = 70 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(CadLayerDefinition.Plot), HeaderText = "Plot", FillWeight = 45 });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(CadLayerDefinition.Enabled), HeaderText = "사용", FillWeight = 45 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(CadLayerDefinition.Description), HeaderText = "설명", FillWeight = 135 });
        return grid;
    }

    private DataGridView CreateRuleGrid()
    {
        DataGridView grid = UiTheme.Grid();
        grid.AutoGenerateColumns = false;
        grid.DataSource = _rules;
        grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(DwgRule.Enabled), HeaderText = "사용", FillWeight = 42 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(DwgRule.Priority), HeaderText = "우선순위", FillWeight = 62 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(DwgRule.Name), HeaderText = "Rule 이름", FillWeight = 105 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(DwgRule.Category), HeaderText = "Category", FillWeight = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(DwgRule.ParameterName), HeaderText = "Parameter", FillWeight = 95 });
        grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = nameof(DwgRule.Operator), HeaderText = "Operator", DataSource = Enum.GetValues<RuleOperator>(), FillWeight = 75 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(DwgRule.CompareValue), HeaderText = "비교값", FillWeight = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(DwgRule.TargetLayer), HeaderText = "Target Layer", FillWeight = 120 });
        return grid;
    }

    private static TabPage CreateTab(string title, DataGridView grid, Action add, Action delete)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(grid);
        var buttons = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        Button addButton = UiTheme.SecondaryButton("추가");
        addButton.Click += (_, _) => add();
        Button deleteButton = UiTheme.SecondaryButton("선택 삭제");
        deleteButton.Click += (_, _) => delete();
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(deleteButton);
        layout.Controls.Add(buttons);
        page.Controls.Add(layout);
        return page;
    }

    private void AddLayer() => _layers.Add(new CadLayerDefinition { Name = "NEW-LAYER", Description = "새 Layer" });
    private void DeleteSelectedLayer() { if (_layerGrid.CurrentRow?.DataBoundItem is CadLayerDefinition item) _layers.Remove(item); }
    private void AddRule() => _rules.Add(new DwgRule { Name = "새 Rule", TargetLayer = _layers.FirstOrDefault()?.Name ?? string.Empty });
    private void DeleteSelectedRule() { if (_ruleGrid.CurrentRow?.DataBoundItem is DwgRule item) _rules.Remove(item); }

    private void SaveProfile()
    {
        ValidateChildren();
        _layerGrid.EndEdit();
        _ruleGrid.EndEdit();
        IReadOnlyList<string> issues = _repository.Validate(_profile);
        if (issues.Count > 0)
        {
            _status.Text = $"저장 중단 · 확인 필요 {issues.Count}건";
            MessageBox.Show(this, string.Join(Environment.NewLine, issues.Take(20)), "Profile 확인 필요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _repository.SaveActive(_profile);
        _status.Text = $"저장 완료 · Layer {_profile.Layers.Count}개 · Rule {_profile.Rules.Count}개";
        MessageBox.Show(this, "회사 CAD Profile을 저장했습니다.", "창Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
