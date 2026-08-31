using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;

namespace ChangExport.UI;

public sealed class LayerAssignForm : Form
{
    private readonly List<CadLayerDefinition> _layers;
    private readonly DataGridView _grid;
    private readonly TextBox _searchBox;

    public string SelectedLayer { get; private set; } = string.Empty;

    public LayerAssignForm(int selectedCount, string currentSummary, IEnumerable<CadLayerDefinition> layers)
    {
        _layers = layers.Where(x => x.Enabled).OrderBy(x => x.Name).ToList();
        Text = "CAD Layer 지정";
        Width = 760;
        Height = 560;
        MinimumSize = new Size(640, 460);
        UiTheme.Apply(this);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var heading = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        heading.Controls.Add(UiTheme.Heading("선택 객체 CAD Layer 지정"));
        heading.Controls.Add(UiTheme.Muted($"선택 {selectedCount:N0}개 · 현재값: {currentSummary}"));
        root.Controls.Add(heading);

        _searchBox = new TextBox { PlaceholderText = "Layer 이름 또는 설명 검색", Dock = DockStyle.Top, Margin = new Padding(0, 14, 0, 8) };
        _searchBox.TextChanged += (_, _) => LoadRows();
        root.Controls.Add(_searchBox);

        root.Controls.Add(UiTheme.Muted("레이어 설정의 속성을 확인하고 적용할 항목을 선택하세요."));

        _grid = UiTheme.Grid();
        _grid.ReadOnly = true;
        _grid.Columns.Add("Name", "Layer");
        _grid.Columns.Add("Color", "ACI 색상");
        _grid.Columns.Add("Linetype", "선종류");
        _grid.Columns.Add("Lineweight", "선가중치(mm)");
        _grid.Columns.Add("Description", "설명");
        _grid.Columns[0].FillWeight = 145;
        _grid.Columns[4].FillWeight = 135;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ApplySelected(); };
        root.Controls.Add(_grid);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        Button apply = UiTheme.PrimaryButton("선택 Layer 적용");
        apply.Click += (_, _) => ApplySelected();
        Button cancel = UiTheme.SecondaryButton("취소");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Button clear = UiTheme.SecondaryButton("값 제거");
        clear.Click += (_, _) => { SelectedLayer = string.Empty; DialogResult = DialogResult.OK; Close(); };
        actions.Controls.Add(apply);
        actions.Controls.Add(cancel);
        actions.Controls.Add(clear);
        root.Controls.Add(actions);

        LoadRows();
        AcceptButton = apply;
        CancelButton = cancel;
    }

    private void LoadRows()
    {
        string query = _searchBox.Text.Trim();
        _grid.Rows.Clear();
        foreach (CadLayerDefinition layer in _layers.Where(x => query.Length == 0 ||
                     x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     x.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            int index = _grid.Rows.Add(layer.Name, layer.ColorIndex, layer.Linetype,
                layer.LineweightMm.ToString("0.00"), layer.Description);
            _grid.Rows[index].Tag = layer;
        }
        if (_grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
    }

    private void ApplySelected()
    {
        if (_grid.CurrentRow?.Tag is not CadLayerDefinition layer)
        {
            MessageBox.Show(this, "적용할 Layer를 선택하세요.", "창Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SelectedLayer = layer.Name;
        DialogResult = DialogResult.OK;
        Close();
    }
}
