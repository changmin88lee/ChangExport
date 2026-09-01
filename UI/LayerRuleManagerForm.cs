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
    private readonly Dictionary<string, List<RevitLayerRow>> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MaterialLayerRule>> _materialDrafts = new(StringComparer.Ordinal);
    private readonly List<MaterialChoice> _materials;
    private readonly HashSet<(string Template, string Search, string Category)> _expanded = new();
    private readonly Dictionary<string, RevitLayerRow> _parents = new();
    private readonly HashSet<string> _expandable = new();
    private readonly ComboBox _setup;
    private readonly TextBox _search;
    private readonly DataGridView _grid;
    private readonly DataGridView _materialGrid;
    private readonly Label _status;
    private readonly Label _materialStatus;
    private string TemplateId => (_setup.SelectedItem as SetupItem)?.Id ?? string.Empty;
    private string SetupName => (_setup.SelectedItem as SetupItem)?.Name ?? string.Empty;
    private sealed record SetupItem(string Id, string Name) { public override string ToString() => Name.Length == 0 ? "기본값" : Name; }
    private sealed record WeightItem(int Value, string Label);

    public LayerRuleManagerForm(ExportConfigurationStore store, RevitExportConfiguration configuration,
        IReadOnlyList<string> setupNames, Func<string, List<RevitLayerRow>> read, IReadOnlyList<MaterialChoice>? materials = null)
        : this(store, configuration, setupNames.Select(name => new LayerTemplateChoice(
            configuration.OutputSetups.FirstOrDefault(s => s.SetupName == name)?.SetupId ?? name, name)).ToList(),
            id => read(configuration.OutputSetups.FirstOrDefault(s => s.SetupId == id)?.SetupName ?? id), materials) { }

    public LayerRuleManagerForm(ExportConfigurationStore store, RevitExportConfiguration configuration,
        IReadOnlyList<LayerTemplateChoice> templates, Func<string, List<RevitLayerRow>> read, IReadOnlyList<MaterialChoice>? materials = null)
    {
        _store = store; _configuration = configuration; _read = read;
        _materials = new() { new MaterialChoice(string.Empty, -1, "(현재 프로젝트에 재료 없음 / 미지정)") };
        _materials.AddRange(materials ?? Array.Empty<MaterialChoice>());
        Text = "DWG 레이어 템플릿"; ClientSize = new Size(1500, 900); MinimumSize = new Size(1180, 720); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 9, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("DWG 레이어 템플릿")); title.Controls.Add(UiTheme.Muted("재료 필터와 카테고리·유형 이름 필터를 한 템플릿에서 관리합니다. 시트에는 이 템플릿을 직접 배정합니다.")); root.Controls.Add(title);
        var profiles = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 8) };
        profiles.Controls.Add(new Label { Text = "템플릿", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }); _setup = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var template in templates) _setup.Items.Add(new SetupItem(template.Id, template.Name)); profiles.Controls.Add(_setup);
        foreach (var (label, action) in new (string, Action)[] { ("새 템플릿", () => AddSetup(false)), ("템플릿 복제", () => AddSetup(true)), ("이름 변경", RenameSetup), ("템플릿 삭제", DeleteSetup), ("파일 불러오기", ImportSetup), ("파일로 저장", ExportSetup) })
        { var button = UiTheme.SecondaryButton(label); button.Click += (_, _) => action(); profiles.Controls.Add(button); } root.Controls.Add(profiles);

        var materialBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
        materialBar.Controls.Add(new Label { Text = "복합벽·복합바닥 재료 필터", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 12, 0) });
        var addMaterial = UiTheme.SecondaryButton("재료 필터 추가"); addMaterial.Click += (_, _) => AddMaterialRule(); materialBar.Controls.Add(addMaterial);
        var removeMaterial = UiTheme.SecondaryButton("재료 필터 삭제"); removeMaterial.Click += (_, _) => RemoveMaterialRule(); materialBar.Controls.Add(removeMaterial);
        _materialStatus = UiTheme.Muted("실제 재료층이 2개 이상인 벽·바닥에만 적용하며 해치는 Revit 표현을 유지합니다."); _materialStatus.Margin = new Padding(15, 9, 0, 0); materialBar.Controls.Add(_materialStatus); root.Controls.Add(materialBar);
        _materialGrid = UiTheme.Grid(); _materialGrid.AutoGenerateColumns = false;
        _materialGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Material", DataPropertyName = nameof(MaterialLayerRule.MaterialUniqueId), HeaderText = "Revit 재료 (정확히 지정)", FillWeight = 180, DataSource = _materials, DisplayMember = nameof(MaterialChoice.Name), ValueMember = nameof(MaterialChoice.UniqueId), FlatStyle = FlatStyle.Flat });
        _materialGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MaterialLayerRule.MaterialName), HeaderText = "저장된 재료명", ReadOnly = true, FillWeight = 130 });
        _materialGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(MaterialLayerRule.Layer), HeaderText = "재료 CAD 레이어", FillWeight = 160 });
        _materialGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "MaterialColor", DataPropertyName = nameof(MaterialLayerRule.Color), HeaderText = "레이어 색상", ReadOnly = true, FillWeight = 80 });
        _materialGrid.CellPainting += PaintMaterialColor; _materialGrid.CellClick += PickMaterialColor; _materialGrid.CellValueChanged += MaterialChanged;
        _materialGrid.CurrentCellDirtyStateChanged += (_, _) => { if (_materialGrid.IsCurrentCellDirty) _materialGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _materialGrid.DataError += (_, e) => { e.ThrowException = false; _materialStatus.Text = "가져온 재료가 현재 프로젝트에 없으면 선택은 비어 있고 저장된 재료명·레이어·색상은 유지됩니다."; }; root.Controls.Add(_materialGrid);

        root.Controls.Add(new Label { Text = "카테고리 / 유형 이름 필터", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) });
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        _search = new TextBox { Width = 300, PlaceholderText = "카테고리 / 하위 항목 / 레이어 검색", Margin = new Padding(0, 3, 8, 3) }; toolbar.Controls.Add(_search);
        foreach (var (label, action) in new (string, Action)[] { ("필터 추가", AddRule), ("필터 삭제", RemoveRule), ("필터 ↑", () => MoveRule(-1)), ("필터 ↓", () => MoveRule(1)) })
        { var button = UiTheme.SecondaryButton(label); button.Click += (_, _) => action(); toolbar.Controls.Add(button); } root.Controls.Add(toolbar);
        _grid = UiTheme.Grid(); _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "Expand", HeaderText = "", Width = 30, MinimumWidth = 30, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ReadOnly = true, FlatStyle = FlatStyle.Flat, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Caption), HeaderText = "카테고리 / 하위 항목", ReadOnly = true, FillWeight = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Contains", DataPropertyName = nameof(RevitLayerRow.TypeNameContains), HeaderText = "유형 이름에 포함된 문자", FillWeight = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Layer), HeaderText = "투영 레이어", FillWeight = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", DataPropertyName = nameof(RevitLayerRow.Color), HeaderText = "투영 색상", ReadOnly = true, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.CutLayer), HeaderText = "절단 레이어", FillWeight = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CutColor", DataPropertyName = nameof(RevitLayerRow.CutColor), HeaderText = "절단 색상", ReadOnly = true, FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(RevitLayerRow.Linetype), HeaderText = "레이어 선종류", FillWeight = 80 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = nameof(RevitLayerRow.LineweightChoice), HeaderText = "선가중치 (mm)", FillWeight = 80, DataSource = new[] { new WeightItem(-1, "원본 유지") }.Concat(RevitLayerMappingService.ValidLineweights.Select(w => new WeightItem(w, (w / 100d).ToString("0.00")))).ToList(), DisplayMember = nameof(WeightItem.Label), ValueMember = nameof(WeightItem.Value), ValueType = typeof(int), FlatStyle = FlatStyle.Flat });
        _grid.CellPainting += PaintColor; _grid.CellClick += PickColor; _grid.CellContentClick += ToggleCategory;
        _grid.CellFormatting += (_, e) => { if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Expand") return; var row = (RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem; e.Value = IsExpandableParent(row) ? (_expanded.Contains((TemplateId, _search.Text.Trim(), row.Category)) ? "−" : "+") : ""; e.FormattingApplied = true; };
        _grid.CellBeginEdit += (_, e) => { if (_grid.Columns[e.ColumnIndex].Name == "Contains" && !((RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem).IsCustom) e.Cancel = true; }; root.Controls.Add(_grid);
        _status = UiTheme.Muted("색상 칸을 클릭하여 선택합니다. 선종류를 비우면 원본 표현을 유지합니다."); _status.Margin = new Padding(0, 8, 0, 0); root.Controls.Add(_status);
        _grid.DataError += (_, e) => { e.ThrowException = false; _status.Text = "입력값을 확인하세요."; };
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var save = UiTheme.PrimaryButton("템플릿 저장"); save.Click += (_, _) => Save(); var close = UiTheme.SecondaryButton("닫기"); close.Click += (_, _) => Close(); actions.Controls.Add(save); actions.Controls.Add(close); root.Controls.Add(actions);
        _setup.SelectedIndexChanged += (_, _) => { LoadRows(); LoadMaterialRows(); }; _search.TextChanged += (_, _) => LoadRows();
        if (_setup.Items.Count == 0) _setup.Items.Add(new SetupItem(Guid.NewGuid().ToString("N"), string.Empty));
        _setup.SelectedItem = _setup.Items.Cast<SetupItem>().FirstOrDefault(s => s.Name == configuration.SelectedOutputSetup) ?? _setup.Items[0];
    }

    private List<RevitLayerRow> Rows(string id) { if (!_drafts.TryGetValue(id, out var rows)) _drafts[id] = rows = _read(id); foreach (var row in rows) row.ViewScope = ""; return rows; }
    private List<MaterialLayerRule> Materials(string id) { if (!_materialDrafts.TryGetValue(id, out var rules)) _materialDrafts[id] = rules = RevitLayerMappingService.ReadMaterialRules(id, _configuration); foreach (var rule in rules) rule.ViewScope = ""; return rules; }
    private void LoadMaterialRows() { var rules = Materials(TemplateId); _materialGrid.DataSource = new BindingList<MaterialLayerRule>(rules); int unresolved = rules.Count(r => string.IsNullOrWhiteSpace(r.MaterialUniqueId)); _materialStatus.Text = $"재료 필터 {rules.Count:N0}개 · 미연결 {unresolved:N0}개 · 복합벽·복합바닥에만 적용"; }
    private void AddMaterialRule()
    {
        if (!_materialGrid.EndEdit()) return; var rules = Materials(TemplateId); var material = _materials.FirstOrDefault(m => m.ElementId >= 0 && m.UniqueId.Length > 0 && rules.All(r => r.MaterialUniqueId != m.UniqueId));
        if (material == null) { _materialStatus.Text = "선택 가능한 현재 프로젝트 재료가 없습니다."; return; } string layer = material.Name; foreach (char invalid in "<>/\\\":;?*|=\r\n") layer = layer.Replace(invalid, '_');
        rules.Add(new MaterialLayerRule { MaterialUniqueId = material.UniqueId, MaterialName = material.Name, Layer = layer, Color = 7, ViewScope = "" }); LoadMaterialRows();
    }
    private void RemoveMaterialRule() { if (_materialGrid.CurrentRow?.DataBoundItem is MaterialLayerRule rule) { Materials(TemplateId).Remove(rule); LoadMaterialRows(); } }
    private void MaterialChanged(object? sender, DataGridViewCellEventArgs e) { if (e.RowIndex < 0 || _materialGrid.Columns[e.ColumnIndex].Name != "Material") return; var rule = (MaterialLayerRule)_materialGrid.Rows[e.RowIndex].DataBoundItem; var material = _materials.FirstOrDefault(m => m.UniqueId == rule.MaterialUniqueId && m.UniqueId.Length > 0); if (material != null) rule.MaterialName = material.Name; }
    private void LoadRows()
    {
        _grid.EndEdit(); try
        {
            var rows = Rows(TemplateId); string term = _search.Text.Trim(); var matched = rows.Where(r => term.Length == 0 || $"{r.Category} {r.Subcategory} {r.Layer} {r.CutLayer} {r.TypeNameContains}".Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToHashSet();
            var filtered = new List<RevitLayerRow>(); _parents.Clear(); _expandable.Clear(); foreach (var group in rows.GroupBy(r => r.Category))
            { var parent = group.FirstOrDefault(r => !r.IsCustom && r.Subcategory.Length == 0) ?? group.First(); var children = group.Where(r => !ReferenceEquals(r, parent) && matched.Contains(r)).ToList(); if (!matched.Contains(parent) && children.Count == 0) continue; _parents[group.Key] = parent; filtered.Add(parent); if (children.Count == 0) continue; _expandable.Add(group.Key); if (_expanded.Contains((TemplateId, term, group.Key))) filtered.AddRange(children); }
            _grid.DataSource = new BindingList<RevitLayerRow>(filtered); _status.Text = $"전체 {rows.Count:N0}개 · 유형 이름 필터 {rows.Count(r => r.IsCustom):N0}개 · 현재 표시 {filtered.Count:N0}행";
        }
        catch (Exception ex) { _grid.DataSource = null; _status.Text = "템플릿 읽기 실패: " + ex.Message; }
    }
    private bool IsExpandableParent(RevitLayerRow row) => _expandable.Contains(row.Category) && _parents.TryGetValue(row.Category, out var parent) && ReferenceEquals(row, parent);
    private void ToggleCategory(object? sender, DataGridViewCellEventArgs e) { if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Expand" || !_grid.EndEdit()) return; var row = (RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem; if (!IsExpandableParent(row)) return; var key = (TemplateId, _search.Text.Trim(), row.Category); if (!_expanded.Add(key)) _expanded.Remove(key); LoadRows(); SelectRule(row); }
    private RevitLayerRow? Selected => _grid.CurrentRow?.DataBoundItem as RevitLayerRow;
    private void AddRule()
    {
        if (Selected is not { } selected || !_grid.EndEdit()) return; var rows = Rows(TemplateId); var parent = rows.FirstOrDefault(r => !r.IsCustom && r.Category == selected.Category && r.Subcategory.Length == 0) ?? selected;
        var rule = parent.Copy(); rule.IsCustom = true; rule.RuleId = Guid.NewGuid().ToString("N"); rule.Subcategory = ""; rule.TypeNameContains = ""; rule.ViewScope = ""; rule.Color = rule.Color is >= 1 and <= 255 ? rule.Color : 7; rule.CutColor = rule.CutColor is >= 1 and <= 255 ? rule.CutColor : rule.Color; if (string.IsNullOrWhiteSpace(rule.CutLayer)) rule.CutLayer = rule.Layer;
        int index = rows.FindLastIndex(r => r.Category == parent.Category && (r.IsCustom || r.Subcategory.Length == 0)); rows.Insert(index + 1, rule); _expanded.Add((TemplateId, "", parent.Category)); if (_search.Text.Length > 0) _search.Clear(); else LoadRows(); SelectRule(rule); _grid.CurrentCell = _grid.CurrentRow!.Cells["Contains"]; _grid.BeginEdit(true);
    }
    private void RemoveRule() { if (Selected is not { IsCustom: true } rule) { _status.Text = "삭제할 유형 이름 필터를 선택하세요."; return; } Rows(TemplateId).Remove(rule); LoadRows(); }
    private void MoveRule(int direction) { if (Selected is not { IsCustom: true } rule || !_grid.EndEdit()) return; var rows = Rows(TemplateId); int current = rows.IndexOf(rule), next = current + direction; if (next < 0 || next >= rows.Count || !rows[next].IsCustom || rows[next].Category != rule.Category) return; (rows[current], rows[next]) = (rows[next], rows[current]); LoadRows(); SelectRule(rule); }
    private void SelectRule(RevitLayerRow rule) { foreach (DataGridViewRow row in _grid.Rows) if (ReferenceEquals(row.DataBoundItem, rule)) { _grid.CurrentCell = row.Cells[0]; break; } }
    private void Save()
    {
        if (!_grid.EndEdit() || !_materialGrid.EndEdit()) return; foreach (var setup in _setup.Items.Cast<SetupItem>()) { Rows(setup.Id); Materials(setup.Id); }
        var issues = new List<string>();
        foreach (var setup in _setup.Items.Cast<SetupItem>())
        {
            var rows = Rows(setup.Id); var materials = Materials(setup.Id);
            issues.AddRange(RevitLayerMappingService.Validate(rows).Select(issue => $"{setup}: {issue}"));
            issues.AddRange(RevitLayerMappingService.ValidateMaterialRules(materials).Select(issue => $"{setup}: {issue}"));
            issues.AddRange(RevitLayerMappingService.ValidateCombined(rows, materials).Select(issue => $"{setup}: {issue}"));
        }
        issues = issues.Distinct().ToList();
        if (issues.Count > 0) { MessageBox.Show(this, string.Join("\n", issues.Take(12)), "템플릿 확인 필요"); return; }
        try
        {
            _configuration.OutputSetups = _setup.Items.Cast<SetupItem>().Select(s => new ExportSetupEdits { SetupId = s.Id, SetupName = s.Name, Layers = Rows(s.Id).Select(Clean).ToList(), MaterialRules = Materials(s.Id).Select(Clean).ToList() }).ToList();
            foreach (var definition in _configuration.OutputSetups) new OutputSetupFile { Name = definition.SetupName.Length == 0 ? "기본값" : definition.SetupName, Layers = definition.Layers, MaterialRules = definition.MaterialRules }.Validate();
            _configuration.SelectedOutputSetup = SetupName; _store.Save(_configuration); _status.Text = "DWG 레이어 템플릿을 저장했습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 실패"); }
    }
    private static RevitLayerRow Clean(RevitLayerRow source) { var copy = source.Copy(); copy.ViewScope = ""; return copy; }
    private static MaterialLayerRule Clean(MaterialLayerRule source) { var copy = source.Copy(); copy.ViewScope = ""; return copy; }
    private bool NameAvailable(string name) => name.Length > 0 && name != "기본값" && !_setup.Items.Cast<SetupItem>().Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private void AddSetup(bool duplicate)
    {
        if (!_grid.EndEdit()) return; using var dialog = new OutputSetupNameForm(duplicate ? "템플릿 복제" : "새 템플릿", duplicate ? _setup.Text + " 복사" : "새 템플릿"); if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!NameAvailable(dialog.SetupName)) { MessageBox.Show(this, "다른 템플릿 이름을 입력하세요."); return; } string id = Guid.NewGuid().ToString("N"); _drafts[id] = (duplicate ? Rows(TemplateId) : _read(id)).Select(Clean).ToList(); _materialDrafts[id] = duplicate ? Materials(TemplateId).Select(Clean).ToList() : new(); var item = new SetupItem(id, dialog.SetupName); _setup.Items.Add(item); _setup.SelectedItem = item;
    }
    private void RenameSetup()
    {
        string id = TemplateId; using var dialog = new OutputSetupNameForm("템플릿 이름 변경", SetupName.Length == 0 ? "기본 템플릿" : SetupName); if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SetupName == SetupName) return;
        if (!NameAvailable(dialog.SetupName)) { MessageBox.Show(this, "다른 템플릿 이름을 입력하세요."); return; } int index = _setup.SelectedIndex; _setup.Items[index] = new SetupItem(id, dialog.SetupName); _setup.SelectedIndex = index;
    }
    private void DeleteSetup()
    {
        if (_setup.Items.Count <= 1) { MessageBox.Show(this, "최소 한 개의 템플릿은 유지해야 합니다."); return; }
        if (_configuration.SheetTemplateIds.Values.Contains(TemplateId, StringComparer.Ordinal) || _configuration.SheetSets.Any(s => s.TemplateId == TemplateId)) { MessageBox.Show(this, "시트 또는 세트에 배정된 템플릿입니다. 먼저 시트 세트 구성에서 다른 템플릿을 지정하세요."); return; }
        string id = TemplateId; int next = Math.Max(0, _setup.SelectedIndex - 1); _setup.Items.RemoveAt(_setup.SelectedIndex); _drafts.Remove(id); _materialDrafts.Remove(id); _setup.SelectedIndex = next;
    }
    private void ImportSetup()
    {
        using var picker = new OpenFileDialog { Filter = "창Export DWG 레이어 템플릿 (*.json)|*.json", CheckFileExists = true }; if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var file = OutputSetupFile.Load(picker.FileName); string name = file.Name == "기본값" ? "기본값 불러오기" : file.Name; string basis = name; for (int n = 2; !NameAvailable(name); n++) name = basis + " " + n;
            string id = Guid.NewGuid().ToString("N"); _drafts[id] = RevitLayerMappingService.MergeCatalog(_read(id), file.Layers.Select(Clean).ToList());
            _materialDrafts[id] = RevitLayerMappingService.RebindImportedMaterials(file.MaterialRules, _materials);
            var item = new SetupItem(id, name); _setup.Items.Add(item); _setup.SelectedItem = item; _status.Text = "템플릿을 불러왔습니다. 재료명 정확히 일치 시 연결했고, 없는 재료는 미연결 상태로 유지했습니다.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "템플릿 불러오기 실패"); }
    }
    private void ExportSetup()
    {
        if (!_grid.EndEdit() || !_materialGrid.EndEdit()) return; using var picker = new SaveFileDialog { Filter = "창Export DWG 레이어 템플릿 (*.json)|*.json", AddExtension = true, DefaultExt = "json", FileName = "ChangExport_DWG레이어템플릿.json", OverwritePrompt = true }; if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { new OutputSetupFile { Version = 3, Name = SetupName.Length == 0 ? "기본값" : SetupName, Layers = Rows(TemplateId).Select(Clean).ToList(), MaterialRules = Materials(TemplateId).Select(Clean).ToList() }.Save(picker.FileName); _status.Text = "DWG 레이어 템플릿 파일을 저장했습니다."; } catch (Exception ex) { MessageBox.Show(this, ex.Message, "템플릿 파일 저장 실패"); }
    }
    private void PickMaterialColor(object? sender, DataGridViewCellEventArgs e) { if (e.RowIndex < 0 || _materialGrid.Columns[e.ColumnIndex].Name != "MaterialColor") return; var rule = (MaterialLayerRule)_materialGrid.Rows[e.RowIndex].DataBoundItem; using var dialog = new AciColorDialog(rule.Color); if (dialog.ShowDialog(this) == DialogResult.OK) { rule.Color = dialog.SelectedIndex; _materialGrid.InvalidateCell(e.ColumnIndex, e.RowIndex); } }
    private void PaintMaterialColor(object? sender, DataGridViewCellPaintingEventArgs e) { if (e.RowIndex < 0 || e.ColumnIndex < 0 || _materialGrid.Columns[e.ColumnIndex].Name != "MaterialColor") return; PaintAciCell(e, Convert.ToInt32(e.Value), false); }
    private void PickColor(object? sender, DataGridViewCellEventArgs e) { if (e.RowIndex < 0 || e.ColumnIndex < 0) return; string name = _grid.Columns[e.ColumnIndex].Name; if (name is not ("Color" or "CutColor")) return; var row = (RevitLayerRow)_grid.Rows[e.RowIndex].DataBoundItem; using var dialog = new AciColorDialog(name == "Color" ? row.Color : row.CutColor); if (dialog.ShowDialog(this) == DialogResult.OK) { if (name == "Color") row.Color = dialog.SelectedIndex; else row.CutColor = dialog.SelectedIndex; _grid.InvalidateCell(e.ColumnIndex, e.RowIndex); } }
    private void PaintColor(object? sender, DataGridViewCellPaintingEventArgs e) { if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name is not ("Color" or "CutColor")) return; PaintAciCell(e, Convert.ToInt32(e.Value), true); }
    private static void PaintAciCell(DataGridViewCellPaintingEventArgs e, int index, bool allowOriginal)
    {
        e.PaintBackground(e.ClipBounds, true); var rect = new Rectangle(e.CellBounds.Left + 7, e.CellBounds.Top + 5, 24, Math.Max(10, e.CellBounds.Height - 10)); using var brush = new SolidBrush(AciPalette.GetColor(index)); e.Graphics!.FillRectangle(brush, rect); e.Graphics.DrawRectangle(Pens.Gray, rect);
        TextRenderer.DrawText(e.Graphics, allowOriginal && index is not (>= 1 and <= 255) ? "원본" : index.ToString(), e.CellStyle!.Font, new Rectangle(rect.Right + 5, e.CellBounds.Top, e.CellBounds.Width - 38, e.CellBounds.Height), (e.State & DataGridViewElementStates.Selected) != 0 ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor, TextFormatFlags.VerticalCenter); e.Paint(e.ClipBounds, DataGridViewPaintParts.Border); e.Handled = true;
    }
}
