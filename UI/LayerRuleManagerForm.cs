using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;
using ChangExport.Standards;

namespace ChangExport.UI;

public sealed class LayerRuleManagerForm : Form
{
    private readonly ExportConfigurationStore _store;
    private readonly RevitExportConfiguration _configuration;
    private readonly Func<string, List<RevitLayerRow>> _read;
    private readonly Dictionary<string, List<RevitLayerRow>> _drafts = new();
    private readonly ComboBox _setup;
    private readonly TextBox _search;
    private readonly DataGridView _grid;
    private readonly Label _status;
    private string SetupName => (_setup.SelectedItem as SetupItem)?.Name ?? string.Empty;
    private sealed record SetupItem(string Name) { public override string ToString() => Name.Length == 0 ? "Revit 기본값" : Name; }
    private sealed record WeightItem(int Value, string Label);

    public LayerRuleManagerForm(ExportConfigurationStore store, RevitExportConfiguration configuration,
        IReadOnlyList<string> setupNames, Func<string, List<RevitLayerRow>> read)
    {
        _store = store; _configuration = configuration; _read = read;
        Text = "DWG 레이어 설정"; ClientSize = new Size(1220, 700); MinimumSize = new Size(1000, 580); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("Revit DWG 레이어 설정"));
        title.Controls.Add(UiTheme.Muted("Revit의 전체 출력 매핑을 사용합니다. 하위 카테고리는 들여쓰기로 표시합니다. 커스텀 필터는 다음 단계입니다.")); root.Controls.Add(title);
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) };
        toolbar.Controls.Add(new Label { Text = "Revit 출력 설정", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _setup = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string name in setupNames) _setup.Items.Add(new SetupItem(name)); toolbar.Controls.Add(_setup);
        _search = new TextBox { Width = 260, PlaceholderText = "카테고리 / 하위 항목 / 레이어 검색", Margin = new Padding(20, 3, 3, 3) };
        toolbar.Controls.Add(_search); root.Controls.Add(toolbar);
        _grid = UiTheme.Grid(); _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Caption), HeaderText = "카테고리 / 하위 항목", ReadOnly = true, FillWeight = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Layer), HeaderText = "투영 레이어", FillWeight = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", DataPropertyName = nameof(RevitLayerRow.Color), HeaderText = "투영 색상", ReadOnly = true, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.CutLayer), HeaderText = "절단 레이어", FillWeight = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CutColor", DataPropertyName = nameof(RevitLayerRow.CutColor), HeaderText = "절단 색상", ReadOnly = true, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Linetype), HeaderText = "레이어 선종류", FillWeight = 80 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = nameof(RevitLayerRow.LineweightChoice), HeaderText = "선가중치 (mm)", FillWeight = 80,
            DataSource = new[] { new WeightItem(-1, "Revit 설정") }.Concat(RevitLayerMappingService.ValidLineweights.Select(w => new WeightItem(w, (w / 100d).ToString("0.00")))).ToList(),
            DisplayMember = nameof(WeightItem.Label), ValueMember = nameof(WeightItem.Value), ValueType = typeof(int), FlatStyle = FlatStyle.Flat });
        _grid.CellPainting += PaintColor; _grid.CellClick += PickColor;
        root.Controls.Add(_grid);
        _status = UiTheme.Muted("색상 칸을 클릭하여 선택합니다. 선종류를 비우면 Revit 설정을 유지합니다.");
        _status.MaximumSize = new Size(1150, 0); _status.Margin = new Padding(0, 10, 0, 0); root.Controls.Add(_status);
        _grid.DataError += (_, e) => { e.ThrowException = false; _status.Text = "입력값을 확인하세요."; };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var save = UiTheme.PrimaryButton("설정 저장"); save.Click += (_, _) => Save();
        var close = UiTheme.SecondaryButton("닫기"); close.Click += (_, _) => Close();
        actions.Controls.Add(save); actions.Controls.Add(close); root.Controls.Add(actions);
        _setup.SelectedIndexChanged += (_, _) => LoadRows(); _search.TextChanged += (_, _) => LoadRows();
        _setup.SelectedItem = _setup.Items.Cast<SetupItem>().FirstOrDefault(s => s.Name == configuration.SelectedSetup) ?? _setup.Items[0];
    }

    private void LoadRows()
    {
        _grid.EndEdit();
        try
        {
            if (!_drafts.TryGetValue(SetupName, out var rows)) _drafts[SetupName] = rows = _read(SetupName);
            string term = _search.Text.Trim();
            var filtered = rows.Where(r => term.Length == 0 || $"{r.Category} {r.Subcategory} {r.Layer} {r.CutLayer}".Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToList();
            _grid.DataSource = new BindingList<RevitLayerRow>(filtered);
            _status.Text = $"전체 매핑 {rows.Count:N0}개 · 표시 {filtered.Count:N0}개 · 색상 칸을 클릭하여 선택 · 선종류/선가중치는 레이어 속성 (객체 Override는 Revit 설정 유지)";
        }
        catch (Exception ex) { _grid.DataSource = null; _status.Text = "설정 읽기 실패: " + ex.Message; }
    }
    private void Save()
    {
        if (!_grid.EndEdit()) return;
        var issues = _drafts.Values.SelectMany(RevitLayerMappingService.Validate).Distinct().ToList();
        if (issues.Count > 0) { MessageBox.Show(this, string.Join("\n", issues.Take(12)), "설정 확인 필요"); return; }
        try
        {
            foreach (var draft in _drafts)
            {
                _configuration.Setups.RemoveAll(s => s.SetupName == draft.Key);
                _configuration.Setups.Add(new ExportSetupEdits { SetupName = draft.Key, Layers = draft.Value.Where(r => r.HasChanges).Select(r => r.Copy()).ToList() });
            }
            _configuration.SelectedSetup = SetupName; _store.Save(_configuration);
            _status.Text = "저장했습니다. Revit 원본 출력 설정과 기존 회사 Profile은 변경하지 않았습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패"); }
    }
    private void PickColor(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        string name = _grid.Columns[e.ColumnIndex].Name;
        if (name != "Color" && name != "CutColor") return;
        var row = (RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem;
        using var dialog = new AciColorDialog(name == "Color" ? row.Color : row.CutColor);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (name == "Color") row.Color = dialog.SelectedIndex; else row.CutColor = dialog.SelectedIndex;
        _grid.InvalidateCell(e.ColumnIndex, e.RowIndex);
    }
    private void PaintColor(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name is not ("Color" or "CutColor")) return;
        e.PaintBackground(e.ClipBounds, true); int index = Convert.ToInt32(e.Value);
        var rect = new Rectangle(e.CellBounds.Left + 7, e.CellBounds.Top + 5, 24, Math.Max(10, e.CellBounds.Height - 10));
        using var brush = new SolidBrush(AciPalette.GetColor(index));
        e.Graphics!.FillRectangle(brush, rect); e.Graphics.DrawRectangle(Pens.Gray, rect);
        TextRenderer.DrawText(e.Graphics, index is >= 1 and <= 255 ? index.ToString() : "원본", e.CellStyle!.Font,
            new Rectangle(rect.Right + 5, e.CellBounds.Top, e.CellBounds.Width - 38, e.CellBounds.Height),
            (e.State & DataGridViewElementStates.Selected) != 0 ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor, TextFormatFlags.VerticalCenter);
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Border); e.Handled = true;
    }
}
