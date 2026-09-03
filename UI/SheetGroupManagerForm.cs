using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;

namespace ChangExport.UI;

public sealed class SheetGroupManagerForm : Form
{
    private sealed record SourceNode(string Key);
    private sealed record SetNode(string Id);
    private sealed record AssignedSheetNode(string SetId);
    private sealed record SpecialNode(string Key);
    private sealed record MemberItem(string Key, string Caption) { public override string ToString() => Caption; }
    private sealed class BufferedTreeView : TreeView
    {
        public BufferedTreeView() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer, true); }
    }

    private readonly SheetSetEditor _editor;
    private readonly Dictionary<string, SheetDescriptor> _sheets;
    private readonly Dictionary<string, List<SheetDescriptor>> _sourceSheets;
    private readonly Dictionary<string, SheetSetDefinition> _setIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sheetOwnerIds = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<LayerTemplateChoice> _templates;
    private readonly Dictionary<string, string> _assignments;
    private readonly List<string> _sourceOrder = new();
    private readonly List<string> _missingSourceOrder = new();
    private readonly HashSet<string> _expandedSources = new(StringComparer.Ordinal) { "host" };
    private readonly BufferedTreeView _tree;
    private readonly TextBox _search;
    private readonly TextBox _newSetName;
    private readonly TextBox _detailName;
    private readonly ComboBox _detailTemplate;
    private readonly ComboBox _detailDirection;
    private readonly ListBox _members;
    private readonly Button _memberUp;
    private readonly Button _memberDown;
    private readonly Label _detailTitle;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _searchTimer;
    private bool _setsExpanded = true;
    private bool _updatingTree;
    private bool _updatingDetails;
    private string _filter = string.Empty;
    private string? _activeSetId;

    public IReadOnlyList<SheetSetDefinition> ResultSets => _editor.Sets.Select(set => set.Copy()).ToList();
    public IReadOnlyDictionary<string, string> ResultAssignments => _assignments;
    public IReadOnlyList<string> ResultSourceOrder => _sourceOrder.Concat(_missingSourceOrder).Distinct(StringComparer.Ordinal).ToList();

    public SheetGroupManagerForm(IEnumerable<SheetDescriptor> sheets, IEnumerable<SheetSetDefinition> sets)
        : this(sheets, sets, Array.Empty<LayerTemplateChoice>(), new Dictionary<string, string>(), Array.Empty<string>()) { }

    public SheetGroupManagerForm(IEnumerable<SheetDescriptor> sheets, IEnumerable<SheetSetDefinition> sets,
        IReadOnlyList<LayerTemplateChoice> templates, IReadOnlyDictionary<string, string> assignments)
        : this(sheets, sets, templates, assignments, Array.Empty<string>()) { }

    public SheetGroupManagerForm(IEnumerable<SheetDescriptor> sheets, IEnumerable<SheetSetDefinition> sets,
        IReadOnlyList<LayerTemplateChoice> templates, IReadOnlyDictionary<string, string> assignments,
        IReadOnlyList<string> sourceOrder)
    {
        var sheetList = sheets.ToList();
        _sheets = sheetList.ToDictionary(sheet => sheet.Key);
        _sourceSheets = sheetList.GroupBy(sheet => sheet.SourceOrderKey).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        _editor = new SheetSetEditor(sets); _templates = templates;
        _assignments = new Dictionary<string, string>(assignments, StringComparer.Ordinal);
        InitializeSourceOrder(sheetList, sourceOrder);

        Text = "시트 세트 구성"; ClientSize = new Size(1100, 740); MinimumSize = new Size(900, 620); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("모델별 시트 · 세트 구성"));
        title.Controls.Add(UiTheme.Muted("현재 프로젝트와 링크 모델을 펼쳐 시트를 찾습니다. 모델 제목을 드래그하면 현재 프로젝트를 포함한 표시 순서를 바꿀 수 있습니다."));
        root.Controls.Add(title);

        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10), WrapContents = false };
        _newSetName = new TextBox { Width = 210, PlaceholderText = "새 세트 이름", Margin = new Padding(0, 6, 8, 0) };
        var create = UiTheme.PrimaryButton("+ 선택 항목으로 세트"); create.Click += (_, _) => Combine();
        var release = UiTheme.SecondaryButton("세트 해제"); release.Click += (_, _) => Release();
        toolbar.Controls.Add(_newSetName); toolbar.Controls.Add(create); toolbar.Controls.Add(release); root.Controls.Add(toolbar);

        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1064, 400) };
        split.Panel1MinSize = 390; split.Panel2MinSize = 350; split.SplitterDistance = 520;
        split.Panel1.BackColor = Color.White; split.Panel2.BackColor = Color.White; root.Controls.Add(split);
        var browser = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), RowCount = 3, ColumnCount = 1 };
        browser.RowStyles.Add(new RowStyle(SizeType.AutoSize)); browser.RowStyles.Add(new RowStyle(SizeType.AutoSize)); browser.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        browser.Controls.Add(new Label { Text = "시트 원본", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = UiTheme.Navy });
        var find = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 8) };
        find.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); find.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "시트 번호·이름·세트 검색", AccessibleName = "시트 검색" };
        var resetOrder = UiTheme.SecondaryButton("기본 순서"); resetOrder.MinimumSize = new Size(82, 30); resetOrder.Height = 30;
        resetOrder.Click += (_, _) => { SetDefaultSourceOrder(); BuildTree(); };
        find.Controls.Add(_search, 0, 0); find.Controls.Add(resetOrder, 1, 0); browser.Controls.Add(find);
        _tree = new BufferedTreeView { Dock = DockStyle.Fill, CheckBoxes = true, FullRowSelect = true, HideSelection = false,
            ItemHeight = 28, BorderStyle = BorderStyle.FixedSingle, AllowDrop = true, AccessibleName = "모델별 시트 목록", ShowNodeToolTips = true };
        _tree.BeforeExpand += (_, e) => { if (e.Node is { } node) PopulateSourceNode(node); };
        _tree.AfterExpand += (_, e) => { if (e.Node is { } node) RememberExpansion(node, true); };
        _tree.AfterCollapse += (_, e) => { if (e.Node is { } node) RememberExpansion(node, false); };
        _tree.AfterCheck += TreeAfterCheck; _tree.AfterSelect += (_, e) => { if (e.Node is { } node) SelectDetails(node); };
        _tree.ItemDrag += TreeItemDrag; _tree.DragEnter += TreeDragOver; _tree.DragOver += TreeDragOver; _tree.DragDrop += TreeDragDrop;
        browser.Controls.Add(_tree); split.Panel1.Controls.Add(browser);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 7 };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize)); details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize)); details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize)); details.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _detailTitle = new Label { Text = "선택한 시트 또는 세트", AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = UiTheme.Navy, Margin = new Padding(0, 0, 0, 12) };
        details.Controls.Add(_detailTitle, 0, 0); details.SetColumnSpan(_detailTitle, 2);
        details.Controls.Add(FieldLabel("이름"), 0, 1); _detailName = new TextBox { Dock = DockStyle.Fill, AccessibleName = "선택 시트 세트 이름" };
        _detailName.TextChanged += (_, _) => ChangeName(); details.Controls.Add(_detailName, 1, 1);
        details.Controls.Add(FieldLabel("DWG 템플릿"), 0, 2); _detailTemplate = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _detailTemplate.Items.Add(new LayerTemplateChoice(string.Empty, "미지정")); foreach (var choice in _templates) _detailTemplate.Items.Add(choice);
        _detailTemplate.SelectedIndexChanged += (_, _) => ChangeTemplate(); details.Controls.Add(_detailTemplate, 1, 2);
        details.Controls.Add(FieldLabel("배치 방향"), 0, 3); _detailDirection = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _detailDirection.Items.AddRange(new object[] { "가로 일렬", "세로 일렬" }); _detailDirection.SelectedIndexChanged += (_, _) => ChangeDirection();
        details.Controls.Add(_detailDirection, 1, 3);
        var membersTitle = new Label { Text = "세트 내부 시트 순서", AutoSize = true, Margin = new Padding(0, 16, 0, 6), Font = new Font(Font, FontStyle.Bold) };
        details.Controls.Add(membersTitle, 0, 4); details.SetColumnSpan(membersTitle, 2);
        _members = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "세트 내부 시트 순서" };
        _members.SelectedIndexChanged += (_, _) => UpdateMoveButtons(); details.Controls.Add(_members, 0, 5); details.SetColumnSpan(_members, 2);
        var move = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        _memberDown = UiTheme.SecondaryButton("아래로 ↓"); _memberDown.Click += (_, _) => MoveMember(1);
        _memberUp = UiTheme.SecondaryButton("위로 ↑"); _memberUp.Click += (_, _) => MoveMember(-1);
        move.Controls.Add(_memberDown); move.Controls.Add(_memberUp); details.Controls.Add(move, 0, 6); details.SetColumnSpan(move, 2);
        split.Panel2.Controls.Add(details);

        _status = UiTheme.Muted("체크한 시트·세트를 묶을 수 있습니다. 이미 구성된 시트는 구성된 세트에서 관리합니다."); root.Controls.Add(_status);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var save = UiTheme.PrimaryButton("세트 저장"); save.Click += (_, _) => Save();
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save); actions.Controls.Add(cancel); root.Controls.Add(actions); CancelButton = cancel;

        _searchTimer = new System.Windows.Forms.Timer { Interval = 180 };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); BuildTree(); };
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        BuildTree();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _searchTimer.Dispose();
        base.Dispose(disposing);
    }

    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 7) };

    private void InitializeSourceOrder(IReadOnlyList<SheetDescriptor> sheets, IReadOnlyList<string> saved)
    {
        var available = sheets.Select(sheet => sheet.SourceOrderKey).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (available.Contains("host") && !saved.Contains("host", StringComparer.Ordinal)) _sourceOrder.Add("host");
        foreach (string key in saved.Where(key => available.Contains(key)).Distinct(StringComparer.Ordinal)) _sourceOrder.Add(key);
        foreach (string key in DefaultSourceOrder().Where(key => !_sourceOrder.Contains(key, StringComparer.Ordinal))) _sourceOrder.Add(key);
        foreach (string key in saved.Where(key => !available.Contains(key)).Distinct(StringComparer.Ordinal)) _missingSourceOrder.Add(key);
    }

    private IEnumerable<string> DefaultSourceOrder() => _sheets.Values.GroupBy(sheet => sheet.SourceOrderKey)
        .OrderBy(group => group.First().IsHost ? 0 : 1).ThenBy(group => group.Min(sheet => sheet.LinkDepth))
        .ThenBy(group => group.First().SourceName, StringComparer.CurrentCultureIgnoreCase).Select(group => group.Key);

    private void SetDefaultSourceOrder()
    { _sourceOrder.Clear(); _sourceOrder.AddRange(DefaultSourceOrder()); }

    private void BuildTree(string? selectSetId = null)
    {
        ReindexSets(); _filter = _search.Text.Trim(); _updatingTree = true; _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            foreach (string key in _sourceOrder)
            {
                var sheets = SourceSheets(key).ToArray();
                if (sheets.Length == 0 || _filter.Length > 0 && !sheets.Any(MatchesSheet)) continue;
                int unassigned = sheets.Count(sheet => FindOwner(sheet.Key)?.SheetUniqueIds.Count == 1);
                var node = new TreeNode(SourceCaption(key, sheets, unassigned)) { Tag = new SourceNode(key), ToolTipText = "드래그하여 모델 표시 순서 변경" };
                node.Nodes.Add(new TreeNode("불러오는 중…")); _tree.Nodes.Add(node);
                if (_filter.Length > 0 || _expandedSources.Contains(key)) node.Expand();
            }
            var combined = _editor.Sets.Where(set => set.SheetUniqueIds.Count > 1 && MatchesSet(set)).ToArray();
            if (combined.Length > 0)
            {
                var group = new TreeNode($"구성된 세트 · {combined.Length:N0}개") { Tag = new SpecialNode("sets") };
                foreach (var set in combined.OrderBy(set => set.Name, StringComparer.CurrentCultureIgnoreCase)) group.Nodes.Add(SetTreeNode(set));
                _tree.Nodes.Add(group); if (_filter.Length > 0 || _setsExpanded) group.Expand();
            }
            SyncVisibleChecks();
            string? wanted = selectSetId ?? _activeSetId;
            TreeNode? selected = wanted == null ? null : DescendantNodes().FirstOrDefault(node => NodeSetId(node) == wanted);
            selected ??= DescendantNodes().FirstOrDefault(node => NodeSetId(node) != null);
            if (selected != null) _tree.SelectedNode = selected; else ShowDetails(null);
        }
        finally { _tree.EndUpdate(); _updatingTree = false; }
        UpdateStatus();
    }

    private void PopulateSourceNode(TreeNode node)
    {
        if (node.Tag is not SourceNode source || node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;
        node.Nodes.Clear();
        foreach (var sheet in SourceSheets(source.Key).Where(MatchesSheet)
            .OrderBy(sheet => sheet.Number, StringComparer.CurrentCultureIgnoreCase).ThenBy(sheet => sheet.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            SheetSetDefinition? owner = FindOwner(sheet.Key); if (owner == null) continue;
            TreeNode child = owner.SheetUniqueIds.Count == 1 ? SetTreeNode(owner, sheet)
                : new TreeNode($"{sheet.Number}   {sheet.Name}   · 세트: {owner.Name}")
                    { Tag = new AssignedSheetNode(owner.Id), ForeColor = Color.FromArgb(112, 119, 130), ToolTipText = "이미 구성된 세트의 시트입니다." };
            node.Nodes.Add(child);
        }
        if (node.Nodes.Count == 0) node.Nodes.Add(new TreeNode("일치하는 시트가 없습니다.") { ForeColor = Color.Gray });
        SyncVisibleChecks();
    }

    private TreeNode SetTreeNode(SheetSetDefinition set, SheetDescriptor? sheet = null)
    {
        string caption = sheet == null ? $"{set.Name}   · {set.SheetUniqueIds.Count:N0}장   · {TemplateName(set.TemplateId)}"
            : $"{sheet.Number}   {sheet.Name}   · {TemplateName(set.TemplateId)}";
        return new TreeNode(caption) { Tag = new SetNode(set.Id), Checked = _editor.SelectedIds.Contains(set.Id) };
    }

    private IEnumerable<SheetDescriptor> SourceSheets(string key) => _sourceSheets.GetValueOrDefault(key) ?? Enumerable.Empty<SheetDescriptor>();
    private SheetSetDefinition? FindOwner(string sheetKey) => _sheetOwnerIds.TryGetValue(sheetKey, out string? id) ? FindSet(id) : null;
    private SheetSetDefinition? FindSet(string? id) => id != null && _setIndex.TryGetValue(id, out var set) ? set : null;
    private void ReindexSets()
    {
        _setIndex.Clear(); _sheetOwnerIds.Clear();
        foreach (var set in _editor.Sets)
        {
            _setIndex[set.Id] = set;
            foreach (string sheet in set.SheetUniqueIds) _sheetOwnerIds[sheet] = set.Id;
        }
    }
    private bool MatchesSheet(SheetDescriptor sheet)
    {
        if (_filter.Length == 0) return true; SheetSetDefinition? owner = FindOwner(sheet.Key);
        return sheet.Number.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
            || sheet.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
            || sheet.SourceName.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
            || owner?.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) == true;
    }
    private bool MatchesSet(SheetSetDefinition set) => _filter.Length == 0 || set.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
        || set.SheetUniqueIds.Any(id => _sheets.TryGetValue(id, out var sheet) && MatchesSheet(sheet));

    private string SourceCaption(string key, IReadOnlyList<SheetDescriptor> sheets, int unassigned)
    {
        var first = sheets[0]; string name = string.IsNullOrWhiteSpace(first.SourceName) ? (first.IsHost ? "프로젝트" : "이름 없는 링크") : first.SourceName;
        string title;
        if (first.IsHost) title = "현재 프로젝트 · " + name;
        else
        {
            int index = _sourceOrder.Where(source => SourceSheets(source).FirstOrDefault()?.IsHost == false).TakeWhile(source => source != key).Count() + 1;
            title = $"링크 {index} · {name}";
        }
        return $"{title}   ({sheets.Count:N0}장 · 미구성 {unassigned:N0}장)";
    }

    private void TreeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_updatingTree || e.Node is not { } node) return; _updatingTree = true;
        try
        {
            if (node.Tag is SetNode setNode)
            { if (node.Checked) _editor.SelectedIds.Add(setNode.Id); else _editor.SelectedIds.Remove(setNode.Id); }
            else if (node.Tag is AssignedSheetNode)
            { node.Checked = false; _status.Text = "이미 구성된 시트입니다. 아래의 '구성된 세트'에서 해당 세트를 선택하세요."; }
            else if (node.Tag is SourceNode source)
            {
                foreach (var set in _editor.Sets.Where(set => set.SheetUniqueIds.Count == 1
                    && _sheets.TryGetValue(set.SheetUniqueIds[0], out var sheet) && sheet.SourceOrderKey == source.Key && MatchesSheet(sheet)))
                    if (node.Checked) _editor.SelectedIds.Add(set.Id); else _editor.SelectedIds.Remove(set.Id);
            }
            else if (node.Tag is SpecialNode { Key: "sets" })
                foreach (var set in _editor.Sets.Where(set => set.SheetUniqueIds.Count > 1 && MatchesSet(set)))
                    if (node.Checked) _editor.SelectedIds.Add(set.Id); else _editor.SelectedIds.Remove(set.Id);
            SyncVisibleChecks();
        }
        finally { _updatingTree = false; }
        UpdateStatus();
    }

    private void SyncVisibleChecks()
    {
        bool previous = _updatingTree; _updatingTree = true;
        try
        {
            foreach (TreeNode node in DescendantNodes())
                if (node.Tag is SetNode set) node.Checked = _editor.SelectedIds.Contains(set.Id);
                else if (node.Tag is AssignedSheetNode) node.Checked = false;
            foreach (TreeNode root in _tree.Nodes)
            {
                IEnumerable<string> ids = root.Tag switch
                {
                    SourceNode source => _editor.Sets.Where(set => set.SheetUniqueIds.Count == 1
                        && _sheets.TryGetValue(set.SheetUniqueIds[0], out var sheet) && sheet.SourceOrderKey == source.Key && MatchesSheet(sheet)).Select(set => set.Id),
                    SpecialNode { Key: "sets" } => _editor.Sets.Where(set => set.SheetUniqueIds.Count > 1 && MatchesSet(set)).Select(set => set.Id),
                    _ => Array.Empty<string>()
                };
                string[] values = ids.ToArray(); root.Checked = values.Length > 0 && values.All(_editor.SelectedIds.Contains);
            }
        }
        finally { _updatingTree = previous; }
    }

    private void SelectDetails(TreeNode node) => ShowDetails(NodeSetId(node));
    private static string? NodeSetId(TreeNode node) => node.Tag switch { SetNode set => set.Id, AssignedSheetNode assigned => assigned.SetId, _ => null };

    private void ShowDetails(string? setId)
    {
        SheetSetDefinition? set = FindSet(setId); _activeSetId = set?.Id; _updatingDetails = true;
        try
        {
            bool enabled = set != null; _detailName.Enabled = enabled; _detailTemplate.Enabled = enabled; _detailDirection.Enabled = enabled; _members.Enabled = enabled;
            _detailTitle.Text = set == null ? "선택한 시트 또는 세트" : set.SheetUniqueIds.Count > 1 ? $"세트 · {set.SheetUniqueIds.Count:N0}장" : "개별 시트";
            _detailName.Text = set?.Name ?? string.Empty;
            _detailTemplate.SelectedItem = set == null ? _detailTemplate.Items[0]
                : _detailTemplate.Items.Cast<LayerTemplateChoice>().FirstOrDefault(item => item.Id == set.TemplateId) ?? _detailTemplate.Items[0];
            _detailDirection.SelectedIndex = set?.Direction == "Vertical" ? 1 : 0; BindMembers(set);
        }
        finally { _updatingDetails = false; }
        UpdateMoveButtons();
    }

    private void BindMembers(SheetSetDefinition? set, int selectedIndex = -1)
    {
        _members.BeginUpdate(); _members.Items.Clear();
        if (set != null)
        for (int index = 0; index < set.SheetUniqueIds.Count; index++)
        {
            string key = set.SheetUniqueIds[index]; string caption = _sheets.TryGetValue(key, out var sheet)
                ? $"{index + 1:00}   {sheet.DisplayNumber}   {sheet.Name}" : $"{index + 1:00}   [현재 로드되지 않은 모델/시트]";
            _members.Items.Add(new MemberItem(key, caption));
        }
        _members.EndUpdate(); if (selectedIndex >= 0 && selectedIndex < _members.Items.Count) _members.SelectedIndex = selectedIndex;
    }

    private void ChangeName()
    {
        if (_updatingDetails) return; var set = FindSet(_activeSetId); if (set == null) return;
        set.Name = _detailName.Text; UpdateVisibleSetNodes(set.Id); UpdateStatus();
    }
    private void ChangeTemplate()
    {
        if (_updatingDetails) return; var set = FindSet(_activeSetId); if (set == null) return;
        _editor.AssignTemplate(set.Id, (_detailTemplate.SelectedItem as LayerTemplateChoice)?.Id ?? string.Empty, _assignments); UpdateVisibleSetNodes(set.Id);
    }
    private void ChangeDirection()
    { if (!_updatingDetails && FindSet(_activeSetId) is { } set) set.Direction = _detailDirection.SelectedIndex == 1 ? "Vertical" : "Horizontal"; }

    private void UpdateVisibleSetNodes(string setId)
    {
        var set = FindSet(setId); if (set == null) return;
        foreach (TreeNode node in DescendantNodes())
        {
            if (node.Tag is SetNode value && value.Id == setId)
            {
                SheetDescriptor? sheet = set.SheetUniqueIds.Count == 1 && _sheets.TryGetValue(set.SheetUniqueIds[0], out var found) ? found : null;
                node.Text = sheet == null ? $"{set.Name}   · {set.SheetUniqueIds.Count:N0}장   · {TemplateName(set.TemplateId)}"
                    : $"{sheet.Number}   {sheet.Name}   · {TemplateName(set.TemplateId)}";
            }
            else if (node.Tag is AssignedSheetNode assigned && assigned.SetId == setId)
            {
                string prefix = node.Text.Split("   · 세트:", StringSplitOptions.None)[0]; node.Text = prefix + "   · 세트: " + set.Name;
            }
        }
    }

    private void MoveMember(int offset)
    {
        var set = FindSet(_activeSetId); int index = _members.SelectedIndex; if (set == null || index < 0) return;
        int target = index + offset; if (target < 0 || target >= set.SheetUniqueIds.Count) return;
        _editor.MoveSheet(set.Id, index, offset); BindMembers(set, target); UpdateMoveButtons();
    }
    private void UpdateMoveButtons()
    { int index = _members.SelectedIndex; _memberUp.Enabled = index > 0; _memberDown.Enabled = index >= 0 && index < _members.Items.Count - 1; }

    private void Combine()
    {
        try { _editor.Combine(_newSetName.Text); string selected = _editor.SelectedIds.Single(); _newSetName.Clear(); BuildTree(selected); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "세트 구성"); }
    }
    private void Release()
    {
        try { _editor.Release(_sheets); BuildTree(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "세트 구성"); }
    }

    private void Save()
    {
        if (_editor.Sets.Any(set => string.IsNullOrWhiteSpace(set.Name))) { MessageBox.Show(this, "세트 이름을 입력하세요."); return; }
        if (_editor.Sets.GroupBy(set => set.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        { MessageBox.Show(this, "같은 세트 이름이 있습니다. 구별되는 이름을 사용하세요."); return; }
        foreach (var set in _editor.Sets)
            foreach (string id in set.SheetUniqueIds)
                if (string.IsNullOrWhiteSpace(set.TemplateId)) _assignments.Remove(id); else _assignments[id] = set.TemplateId;
        DialogResult = DialogResult.OK; Close();
    }

    private void RememberExpansion(TreeNode node, bool expanded)
    {
        if (node.Tag is SourceNode source) { if (expanded) _expandedSources.Add(source.Key); else _expandedSources.Remove(source.Key); }
        else if (node.Tag is SpecialNode { Key: "sets" }) _setsExpanded = expanded;
    }

    private void TreeItemDrag(object? sender, ItemDragEventArgs e)
    { if (e.Item is TreeNode { Parent: null, Tag: SourceNode } node) _tree.DoDragDrop(node, DragDropEffects.Move); }
    private void TreeDragOver(object? sender, DragEventArgs e)
    {
        TreeNode? dragged = e.Data?.GetData(typeof(TreeNode)) as TreeNode; TreeNode? target = RootNodeAt(e.X, e.Y);
        e.Effect = dragged?.Tag is SourceNode && target?.Tag is SourceNode && !ReferenceEquals(dragged, target) ? DragDropEffects.Move : DragDropEffects.None;
    }
    private void TreeDragDrop(object? sender, DragEventArgs e)
    {
        TreeNode? dragged = e.Data?.GetData(typeof(TreeNode)) as TreeNode; TreeNode? target = RootNodeAt(e.X, e.Y);
        if (dragged?.Tag is not SourceNode source || target?.Tag is not SourceNode destination || source.Key == destination.Key) return;
        Point client = _tree.PointToClient(new Point(e.X, e.Y)); bool after = client.Y > target.Bounds.Top + target.Bounds.Height / 2;
        MoveSource(source.Key, destination.Key, after); BuildTree();
    }
    private TreeNode? RootNodeAt(int screenX, int screenY)
    {
        Point client = _tree.PointToClient(new Point(screenX, screenY)); TreeNode? node = _tree.GetNodeAt(client);
        while (node?.Parent != null) node = node.Parent; return node;
    }
    private void MoveSource(string sourceKey, string targetKey, bool after)
    {
        if (!_sourceOrder.Remove(sourceKey)) return; int index = _sourceOrder.IndexOf(targetKey); if (index < 0) { _sourceOrder.Add(sourceKey); return; }
        if (after) index++; _sourceOrder.Insert(Math.Min(index, _sourceOrder.Count), sourceKey);
    }

    private IEnumerable<TreeNode> DescendantNodes()
    {
        IEnumerable<TreeNode> Visit(TreeNodeCollection nodes)
        { foreach (TreeNode node in nodes) { yield return node; foreach (TreeNode child in Visit(node.Nodes)) yield return child; } }
        return Visit(_tree.Nodes);
    }
    private void UpdateStatus() => _status.Text = $"선택 {_editor.SelectedIds.Count:N0}개 · 전체 {_sheets.Count:N0}장 · 세트 {_editor.Sets.Count(set => set.SheetUniqueIds.Count > 1):N0}개";
    private string TemplateName(string id) => id.Length == 0 ? "미지정" : _templates.FirstOrDefault(template => template.Id == id)?.ToString() ?? "없는 템플릿";
}
