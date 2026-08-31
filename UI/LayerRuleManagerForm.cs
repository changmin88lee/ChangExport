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
    private sealed record SetupItem(string Name) { public override string ToString() => Name.Length == 0 ? "기본값" : Name; }
    private sealed record WeightItem(int Value, string Label);

    public LayerRuleManagerForm(ExportConfigurationStore store, RevitExportConfiguration configuration,
        IReadOnlyList<string> setupNames, Func<string, List<RevitLayerRow>> read)
    {
        _store = store; _configuration = configuration; _read = read;
        Text = "DWG 레이어 설정"; ClientSize = new Size(1400, 740); MinimumSize = new Size(1100, 600); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 6, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("창Export DWG 레이어 설정"));
        title.Controls.Add(UiTheme.Muted("Revit 모델·주석 카테고리와 하위 항목입니다. 가져온 CAD 목록은 제외합니다. 같은 카테고리의 필터는 위에서부터 첫 일치만 적용합니다.")); root.Controls.Add(title);
        var profiles = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        foreach (var (label, action) in new (string, Action)[] { ("새 출력 설정", () => AddSetup(false)), ("설정 복제", () => AddSetup(true)),
            ("이름 변경", RenameSetup), ("파일 불러오기", ImportSetup), ("파일로 저장", ExportSetup) })
        { var button = UiTheme.SecondaryButton(label); button.Click += (_, _) => action(); profiles.Controls.Add(button); }
        root.Controls.Add(profiles);
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) };
        toolbar.Controls.Add(new Label { Text = "출력 설정", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
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
            DataSource = new[] { new WeightItem(-1, "원본 유지") }.Concat(RevitLayerMappingService.ValidLineweights.Select(w => new WeightItem(w, (w / 100d).ToString("0.00")))).ToList(),
            DisplayMember = nameof(WeightItem.Label), ValueMember = nameof(WeightItem.Value), ValueType = typeof(int), FlatStyle = FlatStyle.Flat });
        _grid.CellPainting += PaintColor; _grid.CellClick += PickColor;
        _grid.CellBeginEdit += (_, e) => { if (_grid.Columns[e.ColumnIndex].Name == "Contains" && !((RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem).IsCustom) e.Cancel = true; };
        root.Controls.Add(_grid);
        _status = UiTheme.Muted("색상 칸을 클릭하여 선택합니다. 선종류를 비우면 원본 표현을 유지합니다.");
        _status.MaximumSize = new Size(1150, 0); _status.Margin = new Padding(0, 10, 0, 0); root.Controls.Add(_status);
        _grid.DataError += (_, e) => { e.ThrowException = false; _status.Text = "입력값을 확인하세요."; };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var save = UiTheme.PrimaryButton("설정 저장"); save.Click += (_, _) => Save();
        var close = UiTheme.SecondaryButton("닫기"); close.Click += (_, _) => Close();
        actions.Controls.Add(save); actions.Controls.Add(close); root.Controls.Add(actions);
        _setup.SelectedIndexChanged += (_, _) => LoadRows(); _search.TextChanged += (_, _) => LoadRows();
        _setup.SelectedItem = _setup.Items.Cast<SetupItem>().FirstOrDefault(s => s.Name == configuration.SelectedOutputSetup) ?? _setup.Items[0];
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
            var definitions = _setup.Items.Cast<SetupItem>().Select(s => new ExportSetupEdits { SetupName = s.Name,
                Layers = (_drafts.TryGetValue(s.Name, out var rows) ? rows : _read(s.Name)).Select(r => r.Copy()).ToList() }).ToList();
            foreach (var definition in definitions) new OutputSetupFile { Name = definition.SetupName.Length == 0 ? "기본값" : definition.SetupName, Layers = definition.Layers }.Validate();
            _configuration.OutputSetups = definitions;
            _configuration.SelectedOutputSetup = SetupName; _store.Save(_configuration);
            _status.Text = "창Export 출력 설정을 저장했습니다. Revit의 원본 출력 설정은 변경하지 않았습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패"); }
    }
    private bool NameAvailable(string name) => name.Length > 0 && name != "기본값"
        && !_setup.Items.Cast<SetupItem>().Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private void AddSetup(bool duplicate)
    {
        if (!_grid.EndEdit()) return;
        using var dialog = new OutputSetupNameForm(duplicate ? "출력 설정 복제" : "새 출력 설정", duplicate ? _setup.Text + " 복사" : "새 출력 설정");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!NameAvailable(dialog.SetupName)) { MessageBox.Show(this, "다른 설정 이름을 입력하세요."); return; }
        _drafts[dialog.SetupName] = (duplicate ? _drafts[SetupName] : _read("")).Select(r => r.Copy()).ToList();
        var item = new SetupItem(dialog.SetupName); _setup.Items.Add(item); _setup.SelectedItem = item;
        _status.Text = "설정 저장을 누르면 새 출력 설정이 저장됩니다.";
    }
    private void RenameSetup()
    {
        if (SetupName.Length == 0) { MessageBox.Show(this, "기본값의 이름은 유지됩니다. 설정 복제를 사용하세요."); return; }
        if (!_grid.EndEdit()) return;
        using var dialog = new OutputSetupNameForm("출력 설정 이름 변경", SetupName);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SetupName == SetupName) return;
        if (!NameAvailable(dialog.SetupName)) { MessageBox.Show(this, "다른 설정 이름을 입력하세요."); return; }
        string old = SetupName; int index = _setup.SelectedIndex;
        _drafts[dialog.SetupName] = _drafts[old]; _drafts.Remove(old);
        _setup.Items[index] = new SetupItem(dialog.SetupName); _setup.SelectedIndex = index; LoadRows();
    }
    private void ImportSetup()
    {
        using var picker = new OpenFileDialog { Filter = "창Export 출력 설정 (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var file = OutputSetupFile.Load(picker.FileName);
            string name = file.Name == "기본값" ? "기본값 불러오기" : file.Name;
            string basis = name; for (int n = 2; !NameAvailable(name); n++) name = basis + " " + n;
            _drafts[name] = RevitLayerMappingService.MergeCatalog(_read(""), file.Layers);
            var item = new SetupItem(name); _setup.Items.Add(item); _setup.SelectedItem = item;
            _status.Text = "파일을 불러왔습니다. 설정 저장을 누르면 저장됩니다. 기존 설정은 유지했습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "출력 설정 불러오기 실패"); }
    }
    private void ExportSetup()
    {
        if (!_grid.EndEdit()) return;
        using var picker = new SaveFileDialog { Filter = "창Export 출력 설정 (*.json)|*.json", AddExtension = true, DefaultExt = "json",
            FileName = "ChangExport_출력설정.json", OverwritePrompt = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            new OutputSetupFile { Name = SetupName.Length == 0 ? "기본값" : SetupName, Layers = _drafts[SetupName].Select(r => r.Copy()).ToList() }.Save(picker.FileName);
            _status.Text = "창Export 출력 설정 파일을 저장했습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "출력 설정 파일 저장 실패"); }
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
