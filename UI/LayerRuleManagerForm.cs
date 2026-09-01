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
    private readonly Func<string, string, List<RevitLayerRow>> _read;
    private readonly Dictionary<string, List<RevitLayerRow>> _drafts = new();
    private readonly Dictionary<string, List<MaterialLayerRule>> _materialDrafts = new();
    private readonly List<MaterialChoice> _materials;
    private readonly HashSet<(string Setup, string Search, string Category)> _expanded = new();
    private readonly Dictionary<string, RevitLayerRow> _parents = new();
    private readonly HashSet<string> _expandable = new();
    private readonly ComboBox _setup;
    private readonly ComboBox _materialSetup;
    private readonly ComboBox _viewScope;
    private readonly ComboBox _materialViewScope;
    private readonly TextBox _search;
    private readonly DataGridView _grid;
    private readonly DataGridView _materialGrid;
    private readonly Label _status;
    private readonly Label _materialStatus;
    private string SetupName => (_setup.SelectedItem as SetupItem)?.Name ?? string.Empty;
    private string Scope => (_viewScope.SelectedItem as ViewItem)?.Value ?? ViewLayerScope.ArchitecturePlan;
    private string DraftKey(string setup, string scope) => setup + "\u001f" + scope;
    private sealed record SetupItem(string Name) { public override string ToString() => Name.Length == 0 ? "기본값" : Name; }
    private sealed record ViewItem(string Value) { public override string ToString() => ViewLayerScope.Label(Value); }
    private sealed record WeightItem(int Value, string Label);

    private List<RevitLayerRow> ReadRows(string setup, string scope)
    {
        var rows = _read(setup, scope);
        foreach (var row in rows) row.ViewScope = scope;
        return rows;
    }

    public LayerRuleManagerForm(ExportConfigurationStore store, RevitExportConfiguration configuration,
        IReadOnlyList<string> setupNames, Func<string, List<RevitLayerRow>> read, IReadOnlyList<MaterialChoice>? materials = null)
        : this(store, configuration, setupNames, (name, _) => read(name), materials) { }

    public LayerRuleManagerForm(ExportConfigurationStore store, RevitExportConfiguration configuration,
        IReadOnlyList<string> setupNames, Func<string, string, List<RevitLayerRow>> read, IReadOnlyList<MaterialChoice>? materials = null)
    {
        _store = store; _configuration = configuration; _read = read;
        _materials = (materials ?? Array.Empty<MaterialChoice>()).ToList();
        foreach (var saved in configuration.OutputSetups.SelectMany(s => s.MaterialRules ?? new()).Where(r => r.MaterialUniqueId.Length > 0))
            if (_materials.All(m => m.UniqueId != saved.MaterialUniqueId))
                _materials.Add(new MaterialChoice(saved.MaterialUniqueId, -1, saved.MaterialName + " (현재 프로젝트에서 찾을 수 없음)"));
        Text = "DWG 레이어 설정"; ClientSize = new Size(1400, 740); MinimumSize = new Size(1100, 600); UiTheme.Apply(this);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var materialPage = new TabPage("복합벽·복합바닥 재료 필터") { Padding = new Padding(0) };
        var categoryPage = new TabPage("카테고리 / 유형 이름 필터") { Padding = new Padding(0) };
        tabs.TabPages.Add(materialPage); tabs.TabPages.Add(categoryPage); Controls.Add(tabs);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 6, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        categoryPage.Controls.Add(root);
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
        toolbar.Controls.Add(new Label { Text = "평면도 구분", AutoSize = true, Margin = new Padding(16, 8, 8, 0) });
        _viewScope = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string scope in ViewLayerScope.All) _viewScope.Items.Add(new ViewItem(scope)); toolbar.Controls.Add(_viewScope);
        _search = new TextBox { Width = 260, PlaceholderText = "카테고리 / 하위 항목 / 레이어 검색", Margin = new Padding(20, 3, 3, 3) };
        toolbar.Controls.Add(_search); root.Controls.Add(toolbar);
        var add = UiTheme.SecondaryButton("필터 추가"); add.Click += (_, _) => AddRule(); toolbar.Controls.Add(add);
        var remove = UiTheme.SecondaryButton("필터 삭제"); remove.Click += (_, _) => RemoveRule(); toolbar.Controls.Add(remove);
        var up = UiTheme.SecondaryButton("필터 ↑"); up.Click += (_, _) => MoveRule(-1); toolbar.Controls.Add(up);
        var down = UiTheme.SecondaryButton("필터 ↓"); down.Click += (_, _) => MoveRule(1); toolbar.Controls.Add(down);
        _grid = UiTheme.Grid(); _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "Expand", HeaderText = "", Width = 30, MinimumWidth = 30,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ReadOnly = true, FlatStyle = FlatStyle.Flat, SortMode = DataGridViewColumnSortMode.NotSortable });
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
        _grid.CellContentClick += ToggleCategory;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Expand") return;
            var row = (RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem;
            e.Value = IsExpandableParent(row) ? (_expanded.Contains((SetupName, _search.Text.Trim(), row.Category)) ? "−" : "+") : "";
            e.FormattingApplied = true;
        };
        _grid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Expand") return;
            if (IsExpandableParent((RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem)) return;
            e.PaintBackground(e.ClipBounds, true); e.Paint(e.ClipBounds, DataGridViewPaintParts.Border); e.Handled = true;
        };
        _grid.CellBeginEdit += (_, e) => { if (_grid.Columns[e.ColumnIndex].Name == "Contains" && !((RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem).IsCustom) e.Cancel = true; };
        root.Controls.Add(_grid);
        _status = UiTheme.Muted("색상 칸을 클릭하여 선택합니다. 선종류를 비우면 원본 표현을 유지합니다.");
        _status.MaximumSize = new Size(1150, 0); _status.Margin = new Padding(0, 10, 0, 0); root.Controls.Add(_status);
        _grid.DataError += (_, e) => { e.ThrowException = false; _status.Text = "입력값을 확인하세요."; };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var save = UiTheme.PrimaryButton("설정 저장"); save.Click += (_, _) => Save();
        var close = UiTheme.SecondaryButton("닫기"); close.Click += (_, _) => Close();
        actions.Controls.Add(save); actions.Controls.Add(close); root.Controls.Add(actions);
        var materialRoot = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        materialRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); materialRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        materialRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); materialRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); materialRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        materialPage.Controls.Add(materialRoot);
        var materialTitle = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        materialTitle.Controls.Add(UiTheme.Heading("복합벽·복합바닥 재료 필터"));
        materialTitle.Controls.Add(UiTheme.Muted("실제 재료층이 2개 이상인 벽·바닥에서만 정확히 선택한 재료층을 지정 레이어와 색상으로 출력합니다. 단일층과 다른 카테고리는 제외하며 해치는 Revit 표현을 유지합니다."));
        materialRoot.Controls.Add(materialTitle);
        var materialToolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) };
        materialToolbar.Controls.Add(new Label { Text = "출력 설정", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _materialSetup = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string name in setupNames) _materialSetup.Items.Add(new SetupItem(name)); materialToolbar.Controls.Add(_materialSetup);
        materialToolbar.Controls.Add(new Label { Text = "평면도 구분", AutoSize = true, Margin = new Padding(16, 8, 8, 0) });
        _materialViewScope = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string scope in ViewLayerScope.All) _materialViewScope.Items.Add(new ViewItem(scope)); materialToolbar.Controls.Add(_materialViewScope);
        var addMaterial = UiTheme.SecondaryButton("재료 필터 추가"); addMaterial.Click += (_, _) => AddMaterialRule(); materialToolbar.Controls.Add(addMaterial);
        var removeMaterial = UiTheme.SecondaryButton("재료 필터 삭제"); removeMaterial.Click += (_, _) => RemoveMaterialRule(); materialToolbar.Controls.Add(removeMaterial);
        materialRoot.Controls.Add(materialToolbar);
        _materialGrid = UiTheme.Grid(); _materialGrid.AutoGenerateColumns = false;
        _materialGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Material", DataPropertyName = nameof(MaterialLayerRule.MaterialUniqueId),
            HeaderText = "Revit 재료 (정확히 지정)", FillWeight = 180, DataSource = _materials, DisplayMember = nameof(MaterialChoice.Name), ValueMember = nameof(MaterialChoice.UniqueId), FlatStyle = FlatStyle.Flat });
        _materialGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MaterialLayerRule.Layer), HeaderText = "재료 CAD 레이어", FillWeight = 160 });
        _materialGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "MaterialColor", DataPropertyName = nameof(MaterialLayerRule.Color), HeaderText = "레이어 색상", ReadOnly = true, FillWeight = 80 });
        _materialGrid.CellPainting += PaintMaterialColor; _materialGrid.CellClick += PickMaterialColor;
        _materialGrid.CellValueChanged += MaterialChanged; _materialGrid.CurrentCellDirtyStateChanged += (_, _) =>
        { if (_materialGrid.IsCurrentCellDirty) _materialGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        materialRoot.Controls.Add(_materialGrid);
        _materialStatus = UiTheme.Muted("재료 필터를 만들지 않은 재료는 기존 유형/카테고리 레이어를 유지합니다.");
        _materialGrid.DataError += (_, e) => { e.ThrowException = false; _materialStatus.Text = "현재 프로젝트에 존재하는 Revit 재료를 선택하세요."; };
        _materialStatus.Margin = new Padding(0, 10, 0, 0); materialRoot.Controls.Add(_materialStatus);
        var materialActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var materialSave = UiTheme.PrimaryButton("설정 저장"); materialSave.Click += (_, _) => Save();
        var materialClose = UiTheme.SecondaryButton("닫기"); materialClose.Click += (_, _) => Close();
        materialActions.Controls.Add(materialSave); materialActions.Controls.Add(materialClose); materialRoot.Controls.Add(materialActions);
        _setup.SelectedIndexChanged += (_, _) => { if (_materialSetup.SelectedIndex != _setup.SelectedIndex) _materialSetup.SelectedIndex = _setup.SelectedIndex; LoadRows(); LoadMaterialRows(); };
        _materialSetup.SelectedIndexChanged += (_, _) => { if (_setup.SelectedIndex != _materialSetup.SelectedIndex) _setup.SelectedIndex = _materialSetup.SelectedIndex; };
        _viewScope.SelectedIndexChanged += (_, _) => { if (_materialViewScope.SelectedIndex != _viewScope.SelectedIndex) _materialViewScope.SelectedIndex = _viewScope.SelectedIndex; LoadRows(); LoadMaterialRows(); };
        _materialViewScope.SelectedIndexChanged += (_, _) => { if (_viewScope.SelectedIndex != _materialViewScope.SelectedIndex) _viewScope.SelectedIndex = _materialViewScope.SelectedIndex; };
        _search.TextChanged += (_, _) => LoadRows();
        _viewScope.SelectedIndex = 0;
        _setup.SelectedItem = _setup.Items.Cast<SetupItem>().FirstOrDefault(s => s.Name == configuration.SelectedOutputSetup) ?? _setup.Items[0];
    }

    private void LoadMaterialRows()
    {
        string key = DraftKey(SetupName, Scope);
        if (!_materialDrafts.TryGetValue(key, out var rules))
            _materialDrafts[key] = rules = RevitLayerMappingService.ReadMaterialRules(SetupName, _configuration, Scope);
        _materialGrid.DataSource = new BindingList<MaterialLayerRule>(rules);
        _materialStatus.Text = $"{ViewLayerScope.Label(Scope)} · 재료 필터 {rules.Count:N0}개 · 복합벽·복합바닥에만 적용됩니다.";
    }

    private void AddMaterialRule()
    {
        if (!_materialGrid.EndEdit()) return;
        if (_materials.Count == 0) { _materialStatus.Text = "현재 프로젝트에서 선택할 Revit 재료를 찾지 못했습니다."; return; }
        var rules = _materialDrafts[DraftKey(SetupName, Scope)];
        var material = _materials.FirstOrDefault(m => m.ElementId >= 0 && rules.All(r => r.MaterialUniqueId != m.UniqueId));
        if (material == null) { _materialStatus.Text = "현재 프로젝트의 모든 재료가 이미 지정되었습니다."; return; }
        string layer = material.Name;
        foreach (char invalid in "<>/\\\":;?*|=\r\n") layer = layer.Replace(invalid, '_');
        var rule = new MaterialLayerRule { MaterialUniqueId = material.UniqueId, MaterialName = material.Name, Layer = layer, Color = 7, ViewScope = Scope };
        rules.Add(rule); LoadMaterialRows();
        _materialGrid.CurrentCell = _materialGrid.Rows[^1].Cells["Material"];
    }

    private void RemoveMaterialRule()
    {
        if (_materialGrid.CurrentRow?.DataBoundItem is not MaterialLayerRule rule) return;
        _materialDrafts[DraftKey(SetupName, Scope)].Remove(rule); LoadMaterialRows();
    }

    private void MaterialChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _materialGrid.Columns[e.ColumnIndex].Name != "Material") return;
        var rule = (MaterialLayerRule)_materialGrid.Rows[e.RowIndex].DataBoundItem;
        var material = _materials.FirstOrDefault(m => m.UniqueId == rule.MaterialUniqueId);
        if (material != null) rule.MaterialName = material.Name;
    }

    private void PickMaterialColor(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _materialGrid.Columns[e.ColumnIndex].Name != "MaterialColor") return;
        var rule = (MaterialLayerRule)_materialGrid.Rows[e.RowIndex].DataBoundItem;
        using var dialog = new AciColorDialog(rule.Color);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        rule.Color = dialog.SelectedIndex; _materialGrid.InvalidateCell(e.ColumnIndex, e.RowIndex);
    }

    private void PaintMaterialColor(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _materialGrid.Columns[e.ColumnIndex].Name != "MaterialColor") return;
        e.PaintBackground(e.ClipBounds, true); int index = Convert.ToInt32(e.Value);
        var rect = new Rectangle(e.CellBounds.Left + 7, e.CellBounds.Top + 5, 24, Math.Max(10, e.CellBounds.Height - 10));
        using var brush = new SolidBrush(AciPalette.GetColor(index));
        e.Graphics!.FillRectangle(brush, rect); e.Graphics.DrawRectangle(Pens.Gray, rect);
        TextRenderer.DrawText(e.Graphics, index.ToString(), e.CellStyle!.Font, new Rectangle(rect.Right + 5, e.CellBounds.Top, e.CellBounds.Width - 38, e.CellBounds.Height),
            (e.State & DataGridViewElementStates.Selected) != 0 ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor, TextFormatFlags.VerticalCenter);
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Border); e.Handled = true;
    }

    private void LoadRows()
    {
        _grid.EndEdit();
        try
        {
            string key = DraftKey(SetupName, Scope);
            if (!_drafts.TryGetValue(key, out var rows)) _drafts[key] = rows = ReadRows(SetupName, Scope);
            string term = _search.Text.Trim();
            var matched = rows.Where(r => term.Length == 0 || $"{r.Category} {r.Subcategory} {r.Layer} {r.CutLayer} {r.TypeNameContains}".Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToHashSet();
            var filtered = new List<RevitLayerRow>(); _parents.Clear(); _expandable.Clear();
            foreach (var group in rows.GroupBy(r => r.Category))
            {
                var parent = group.FirstOrDefault(r => !r.IsCustom && r.Subcategory.Length == 0) ?? group.First();
                var children = group.Where(r => !ReferenceEquals(r, parent) && matched.Contains(r)).ToList();
                if (!matched.Contains(parent) && children.Count == 0) continue;
                _parents[group.Key] = parent; filtered.Add(parent);
                if (children.Count == 0) continue;
                _expandable.Add(group.Key);
                if (_expanded.Contains((SetupName, term, group.Key))) filtered.AddRange(children);
            }
            _grid.DataSource = new BindingList<RevitLayerRow>(filtered);
            _status.Text = $"전체 항목 {rows.Count:N0}개 · 필터 {rows.Count(r => r.IsCustom):N0}개 · "
                + (term.Length > 0 ? $"검색 일치 {matched.Count:N0}개 · " : "")
                + $"현재 표시 {filtered.Count:N0}행 · +로 하위 항목 펼치기 · 색상 칸을 클릭하여 선택";
        }
        catch (Exception ex) { _grid.DataSource = null; _status.Text = "설정 읽기 실패: " + ex.Message; }
    }
    private bool IsExpandableParent(RevitLayerRow row) => _expandable.Contains(row.Category)
        && _parents.TryGetValue(row.Category, out var parent) && ReferenceEquals(row, parent);
    private void ToggleCategory(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Expand" || !_grid.EndEdit()) return;
        var row = (RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem;
        if (!IsExpandableParent(row)) return;
        var key = (SetupName, _search.Text.Trim(), row.Category);
        if (!_expanded.Add(key)) _expanded.Remove(key);
        LoadRows(); SelectRule(row);
    }
    private RevitLayerRow? Selected => _grid.CurrentRow?.DataBoundItem as RevitLayerRow;
    private void AddRule()
    {
        if (Selected is not { } selected || !_grid.EndEdit()) return;
        var rows = _drafts[DraftKey(SetupName, Scope)];
        var parent = rows.FirstOrDefault(r => !r.IsCustom && r.Category == selected.Category && r.Subcategory.Length == 0) ?? selected;
        var rule = parent.Copy(); rule.IsCustom = true; rule.RuleId = Guid.NewGuid().ToString("N"); rule.Subcategory = "";
        rule.TypeNameContains = ""; rule.Color = rule.Color is >= 1 and <= 255 ? rule.Color : 7;
        rule.CutColor = rule.CutColor is >= 1 and <= 255 ? rule.CutColor : rule.Color;
        if (string.IsNullOrWhiteSpace(rule.CutLayer)) rule.CutLayer = rule.Layer;
        int index = rows.FindLastIndex(r => r.Category == parent.Category && (r.IsCustom || r.Subcategory.Length == 0));
        rows.Insert(index + 1, rule); _expanded.Add((SetupName, "", parent.Category));
        // A newly created empty rule must remain visible for editing, even during search.
        if (_search.Text.Length > 0) _search.Clear(); else LoadRows();
        SelectRule(rule);
        _grid.CurrentCell = _grid.CurrentRow!.Cells["Contains"]; _grid.BeginEdit(true);
    }
    private void RemoveRule()
    {
        if (Selected is not { IsCustom: true } rule) { _status.Text = "기본 카테고리는 삭제할 수 없습니다. 삭제할 필터 행을 선택하세요."; return; }
        _drafts[DraftKey(SetupName, Scope)].Remove(rule); LoadRows();
    }
    private void MoveRule(int direction)
    {
        if (Selected is not { IsCustom: true } rule || !_grid.EndEdit()) return;
        var rows = _drafts[DraftKey(SetupName, Scope)]; int current = rows.IndexOf(rule), next = current + direction;
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
        if (!_grid.EndEdit() || !_materialGrid.EndEdit()) return;
        foreach (var setup in _setup.Items.Cast<SetupItem>())
        foreach (string scope in ViewLayerScope.All)
        {
            string key = DraftKey(setup.Name, scope);
            if (!_drafts.ContainsKey(key)) _drafts[key] = ReadRows(setup.Name, scope);
            if (!_materialDrafts.ContainsKey(key)) _materialDrafts[key] = RevitLayerMappingService.ReadMaterialRules(setup.Name, _configuration, scope);
        }
        var issues = RevitLayerMappingService.Validate(_drafts.Values.SelectMany(rows => rows)).Distinct().ToList();
        issues.AddRange(RevitLayerMappingService.ValidateMaterialRules(_materialDrafts.Values.SelectMany(rules => rules)));
        issues.AddRange(RevitLayerMappingService.ValidateCombined(_drafts.Values.SelectMany(rows => rows),
            _materialDrafts.Values.SelectMany(rules => rules)));
        if (issues.Count > 0) { MessageBox.Show(this, string.Join("\n", issues.Take(12)), "설정 확인 필요"); return; }
        try
        {
            var definitions = _setup.Items.Cast<SetupItem>().Select(s => new ExportSetupEdits { SetupName = s.Name,
                Layers = ViewLayerScope.All.SelectMany(scope => _drafts[DraftKey(s.Name, scope)]).Select(r => r.Copy()).ToList(),
                MaterialRules = ViewLayerScope.All.SelectMany(scope => _materialDrafts[DraftKey(s.Name, scope)]).Select(r => r.Copy()).ToList() }).ToList();
            foreach (var definition in definitions) new OutputSetupFile { Name = definition.SetupName.Length == 0 ? "기본값" : definition.SetupName,
                Layers = definition.Layers, MaterialRules = definition.MaterialRules }.Validate();
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
        foreach (string scope in ViewLayerScope.All)
        {
            string sourceKey = DraftKey(SetupName, scope), targetKey = DraftKey(dialog.SetupName, scope);
            if (!_drafts.TryGetValue(sourceKey, out var sourceRows)) _drafts[sourceKey] = sourceRows = ReadRows(SetupName, scope);
            if (!_materialDrafts.TryGetValue(sourceKey, out var sourceMaterials))
                _materialDrafts[sourceKey] = sourceMaterials = RevitLayerMappingService.ReadMaterialRules(SetupName, _configuration, scope);
            _drafts[targetKey] = (duplicate ? sourceRows : ReadRows("", scope)).Select(r => r.Copy()).ToList();
            _materialDrafts[targetKey] = (duplicate ? sourceMaterials : RevitLayerMappingService.ReadMaterialRules("", _configuration, scope)).Select(r => r.Copy()).ToList();
        }
        var item = new SetupItem(dialog.SetupName); _setup.Items.Add(item); _materialSetup.Items.Add(item); _setup.SelectedItem = item;
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
        foreach (string scope in ViewLayerScope.All)
        {
            string oldKey = DraftKey(old, scope), newKey = DraftKey(dialog.SetupName, scope);
            if (!_drafts.TryGetValue(oldKey, out var rows)) rows = ReadRows(old, scope);
            if (!_materialDrafts.TryGetValue(oldKey, out var materials)) materials = RevitLayerMappingService.ReadMaterialRules(old, _configuration, scope);
            _drafts[newKey] = rows; _drafts.Remove(oldKey);
            _materialDrafts[newKey] = materials; _materialDrafts.Remove(oldKey);
        }
        var renamed = new SetupItem(dialog.SetupName); _setup.Items[index] = renamed; _materialSetup.Items[index] = renamed; _setup.SelectedIndex = index; LoadRows(); LoadMaterialRows();
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
            foreach (string scope in ViewLayerScope.All)
            {
                var fileRows = file.Layers.Where(r => string.IsNullOrEmpty(r.ViewScope) || r.ViewScope == scope)
                    .Select(r => { var copy = r.Copy(); copy.ViewScope = scope; return copy; }).ToList();
                _drafts[DraftKey(name, scope)] = RevitLayerMappingService.MergeCatalog(ReadRows("", scope), fileRows);
                _materialDrafts[DraftKey(name, scope)] = file.MaterialRules.Where(r => string.IsNullOrEmpty(r.ViewScope) || r.ViewScope == scope)
                    .Select(r => { var copy = r.Copy(); copy.ViewScope = scope; return copy; }).ToList();
            }
            var item = new SetupItem(name); _setup.Items.Add(item); _materialSetup.Items.Add(item); _setup.SelectedItem = item;
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
            foreach (string scope in ViewLayerScope.All)
            {
                string key = DraftKey(SetupName, scope);
                if (!_drafts.ContainsKey(key)) _drafts[key] = ReadRows(SetupName, scope);
                if (!_materialDrafts.ContainsKey(key)) _materialDrafts[key] = RevitLayerMappingService.ReadMaterialRules(SetupName, _configuration, scope);
            }
            new OutputSetupFile { Name = SetupName.Length == 0 ? "기본값" : SetupName,
                Layers = ViewLayerScope.All.SelectMany(scope => _drafts[DraftKey(SetupName, scope)]).Select(r => r.Copy()).ToList(),
                MaterialRules = ViewLayerScope.All.SelectMany(scope => _materialDrafts[DraftKey(SetupName, scope)]).Select(r => r.Copy()).ToList() }.Save(picker.FileName);
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
