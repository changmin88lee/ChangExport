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
        Text = "DWG 레이어 설정"; ClientSize = new Size(1400, 740); MinimumSize = new Size(1100, 600); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("Revit DWG 레이어 설정"));
        title.Controls.Add(UiTheme.Muted("Revit 모델·주석 카테고리와 하위 항목입니다. 가져온 CAD 목록은 제외합니다. 같은 카테고리의 필터는 위에서부터 첫 일치만 적용합니다.")); root.Controls.Add(title);
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) };
        toolbar.Controls.Add(new Label { Text = "Revit 출력 설정", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _setup = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string name in setupNames) _setup.Items.Add(new SetupItem(name)); toolbar.Controls.Add(_setup);
        _search = new TextBox { Width = 260, PlaceholderText = "카테고리 / 하위 항목 / 레이어 검색", Margin = new Padding(20, 3, 3, 3) };
        toolbar.Controls.Add(_search); root.Controls.Add(toolbar);
        var add = UiTheme.SecondaryButton("필터 추가"); add.Click += (_, _) => AddRule(); toolbar.Controls.Add(add);
        var remove = UiTheme.SecondaryButton("필터 삭제"); remove.Click += (_, _) => RemoveRule(); toolbar.Controls.Add(remove);
        var up = UiTheme.SecondaryButton("필터 ↑"); up.Click += (_, _) => MoveRule(-1); toolbar.Controls.Add(up);
        var down = UiTheme.SecondaryButton("필터 ↓"); down.Click += (_, _) => MoveRule(1); toolbar.Controls.Add(down);
        _grid = UiTheme.Grid(); _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Caption), HeaderText = "카테고리 / 하위 항목", ReadOnly = true, FillWeight = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Contains", DataPropertyName = nameof(RevitLayerRow.TypeNameContains), HeaderText = "유형 이름에 포함된 문자", FillWeight = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Layer), HeaderText = "투영 레이어", FillWeight = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", DataPropertyName = nameof(RevitLayerRow.Color), HeaderText = "투영 색상", ReadOnly = true, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.CutLayer), HeaderText = "절단 레이어", FillWeight = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CutColor", DataPropertyName = nameof(RevitLayerRow.CutColor), HeaderText = "절단 색상", ReadOnly = true, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Linetype), HeaderText = "레이어 선종류", FillWeight = 80 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = nameof(RevitLayerRow.LineweightChoice), HeaderText = "선가중치 (mm)", FillWeight = 80,
            DataSource = new[] { new WeightItem(-1, "Revit 설정") }.Concat(RevitLayerMappingService.ValidLineweights.Select(w => new WeightItem(w, (w / 100d).ToString("0.00")))).ToList(),
            DisplayMember = nameof(WeightItem.Label), ValueMember = nameof(WeightItem.Value), ValueType = typeof(int), FlatStyle = FlatStyle.Flat });
        _grid.CellPainting += PaintColor; _grid.CellClick += PickColor;
        _grid.CellBeginEdit += (_, e) => { if (_grid.Columns[e.ColumnIndex].Name == "Contains" && !((RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem).IsCustom) e.Cancel = true; };
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
            var matched = rows.Where(r => term.Length == 0 || $"{r.Category} {r.Subcategory} {r.Layer} {r.CutLayer} {r.TypeNameContains}".Contains(term, StringComparison.CurrentCultureIgnoreCase))
                .Select(r => r.Category).ToHashSet();
            var filtered = rows.Where(r => matched.Contains(r.Category)).ToList();
            _grid.DataSource = new BindingList<RevitLayerRow>(filtered);
            _status.Text = $"카테고리·하위 항목 {rows.Count(r => !r.IsCustom):N0}개 · 필터 {rows.Count(r => r.IsCustom):N0}개 · 대소문자 구분 없이 포함 · 색상 칸을 클릭하여 선택";
        }
        catch (Exception ex) { _grid.DataSource = null; _status.Text = "설정 읽기 실패: " + ex.Message; }
    }
    private RevitLayerRow? Selected => _grid.CurrentRow?.DataBoundItem as RevitLayerRow;
    private void AddRule()
    {
        if (Selected is not { } selected || !_grid.EndEdit()) return;
        var rows = _drafts[SetupName];
        var parent = rows.FirstOrDefault(r => !r.IsCustom && r.Category == selected.Category && r.Subcategory.Length == 0) ?? selected;
        var rule = parent.Copy(); rule.IsCustom = true; rule.RuleId = Guid.NewGuid().ToString("N"); rule.Subcategory = "";
        rule.TypeNameContains = ""; rule.Color = rule.Color is >= 1 and <= 255 ? rule.Color : 7;
        rule.CutColor = rule.CutColor is >= 1 and <= 255 ? rule.CutColor : rule.Color;
        if (string.IsNullOrWhiteSpace(rule.CutLayer)) rule.CutLayer = rule.Layer;
        int index = rows.FindLastIndex(r => r.Category == parent.Category && (r.IsCustom || r.Subcategory.Length == 0));
        rows.Insert(index + 1, rule); LoadRows(); SelectRule(rule);
        _grid.CurrentCell = _grid.CurrentRow!.Cells["Contains"]; _grid.BeginEdit(true);
    }
    private void RemoveRule()
    {
        if (Selected is not { IsCustom: true } rule) { _status.Text = "기본 카테고리는 삭제할 수 없습니다. 삭제할 필터 행을 선택하세요."; return; }
        _drafts[SetupName].Remove(rule); LoadRows();
    }
    private void MoveRule(int direction)
    {
        if (Selected is not { IsCustom: true } rule || !_grid.EndEdit()) return;
        var rows = _drafts[SetupName]; int current = rows.IndexOf(rule), next = current + direction;
        if (next < 0 || next >= rows.Count || !rows[next].IsCustom || rows[next].Category != rule.Category) return;
        (rows[current], rows[next]) = (rows[next], rows[current]); LoadRows(); SelectRule(rule);
    }
    private void SelectRule(RevitLayerRow rule)
    {
        foreach (DataGridViewRow row in _grid.Rows)
            if (ReferenceEquals(row.DataBoundItem, rule)) { _grid.CurrentCell = row.Cells[0]; break; }
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
                var hidden = _configuration.Setups.FirstOrDefault(s => s.SetupName == draft.Key)?.Layers
                    .Where(r => r.CategoryGroup is "Imported" or "Modifier").Select(r => r.Copy()).ToList() ?? new();
                _configuration.Setups.RemoveAll(s => s.SetupName == draft.Key);
                _configuration.Setups.Add(new ExportSetupEdits { SetupName = draft.Key, Layers = draft.Value.Where(r => r.HasChanges).Select(r => r.Copy()).Concat(hidden).ToList() });
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
