using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;

namespace ChangExport.UI;

public sealed class SheetSpacingSettingsForm : Form
{
    private readonly BindingList<ExportSetChoice> _choices;
    private readonly DataGridView _grid;
    private readonly NumericUpDown _spacing;
    private bool _selecting;
    public IReadOnlyList<SheetSetDefinition> ResultSets => _choices.Select(c => c.Set.Copy()).ToList();

    public SheetSpacingSettingsForm(IEnumerable<SheetSetDefinition> sets)
    {
        _choices = new(sets.Select(s => new ExportSetChoice { Set = s.Copy() }).ToList());
        Text = "배치 간격 설정"; ClientSize = new Size(680, 460); MinimumSize = new Size(580, 400); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
        root.Controls.Add(UiTheme.Heading("시트 사이 간격"));
        var notice = UiTheme.Muted("기본값 0 mm: 가로·세로 모두 시트 바깥 경계를 붙입니다.\n도곽 안쪽 여백은 유지됩니다. 단위는 축척 적용 후 모형공간 mm입니다.");
        notice.Margin = new Padding(0, 4, 0, 12); root.Controls.Add(notice);
        _grid = UiTheme.Grid(); _grid.AutoGenerateColumns = false; _grid.ReadOnly = true; _grid.MultiSelect = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Name), HeaderText = "세트 이름", FillWeight = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Direction), HeaderText = "배치 방향", FillWeight = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ExportSetChoice.Margin), HeaderText = "간격 mm", FillWeight = 90 });
        root.Controls.Add(_grid);
        var edit = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        edit.Controls.Add(new Label { Text = "선택한 세트 간격 (mm)", AutoSize = true, Margin = new Padding(0, 7, 10, 0) });
        _spacing = new NumericUpDown { Width = 130, Minimum = 0, Maximum = 100000, DecimalPlaces = 0,
            ThousandsSeparator = true, AccessibleName = "선택한 세트 간격 mm" };
        _spacing.ValueChanged += (_, _) =>
        {
            if (_selecting || _grid.CurrentRow?.DataBoundItem is not ExportSetChoice choice) return;
            choice.Set.MarginMm = (double)_spacing.Value; _grid.Refresh();
        };
        edit.Controls.Add(_spacing); root.Controls.Add(edit);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0) };
        var save = UiTheme.PrimaryButton("적용"); save.Click += (_, _) => { ValidateChildren(); DialogResult = DialogResult.OK; Close(); };
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        var reset = UiTheme.SecondaryButton("전체 간격 0"); reset.Click += (_, _) =>
        { foreach (var choice in _choices) choice.Set.MarginMm = 0; SelectSpacing(); _grid.Refresh(); };
        actions.Controls.Add(save); actions.Controls.Add(cancel); actions.Controls.Add(reset); root.Controls.Add(actions);
        AcceptButton = save; CancelButton = cancel;
        _grid.CurrentCellChanged += (_, _) => SelectSpacing(); _grid.DataSource = _choices; SelectSpacing();
    }

    private void SelectSpacing()
    {
        _selecting = true;
        try
        {
            var choice = _grid.CurrentRow?.DataBoundItem as ExportSetChoice;
            _spacing.Enabled = choice is not null;
            _spacing.Value = (decimal)Math.Clamp(choice?.Set.MarginMm ?? 0, 0, 100000);
        }
        finally { _selecting = false; }
    }
}
