using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;

namespace ChangExport.UI;

public sealed class SheetGroupManagerForm : Form
{
    private readonly SheetSetEditor _editor;
    private readonly Dictionary<string, SheetDescriptor> _sheets;
    private readonly FlowLayoutPanel _cards;
    private readonly TextBox _name;
    private readonly Label _status;
    public IReadOnlyList<SheetSetDefinition> ResultSets => _editor.Sets.Select(s => s.Copy()).ToList();

    public SheetGroupManagerForm(IEnumerable<SheetDescriptor> sheets, IEnumerable<SheetSetDefinition> sets)
    {
        _sheets = sheets.ToDictionary(s => s.UniqueId); _editor = new SheetSetEditor(sets);
        Text = "시트 세트 구성"; ClientSize = new Size(1000, 720); MinimumSize = new Size(860, 580); UiTheme.Apply(this);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("시트를 선택하여 세트로 묶으세요"));
        title.Controls.Add(UiTheme.Muted("클릭: 하나 선택 · Ctrl: 여러 개 선택 · Shift: 범위 선택 · 세트당 DWG 하나를 모형공간에 배치합니다.")); root.Controls.Add(title);
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 10) };
        _name = new TextBox { Width = 180, PlaceholderText = "새 세트 이름", Margin = new Padding(0, 6, 8, 0) }; toolbar.Controls.Add(_name);
        var create = UiTheme.PrimaryButton("+ 선택 시트 세트"); create.Click += (_, _) => Act(() => _editor.Combine(_name.Text));
        var release = UiTheme.SecondaryButton("세트 해제"); release.Click += (_, _) => Act(() => _editor.Release(_sheets));
        toolbar.Controls.Add(create); toolbar.Controls.Add(release); root.Controls.Add(toolbar);
        _cards = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _cards.ClientSizeChanged += (_, _) => ResizeCards(); root.Controls.Add(_cards);
        _status = UiTheme.Muted("순서는 각 시트의 위/아래 버튼으로 변경합니다. 간격 단위는 확대 후 모형공간 mm입니다."); root.Controls.Add(_status);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var save = UiTheme.PrimaryButton("세트 저장"); save.Click += (_, _) => Save();
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(save); actions.Controls.Add(cancel); root.Controls.Add(actions); CancelButton = cancel;
        Render();
    }
    private void Act(Action action)
    {
        try { action(); Render(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "세트 구성"); }
    }
    private void Save()
    {
        if (_editor.Sets.Any(s => string.IsNullOrWhiteSpace(s.Name))) { MessageBox.Show(this, "세트 이름을 입력하세요."); return; }
        if (_editor.Sets.GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        { MessageBox.Show(this, "같은 세트 이름이 있습니다. 구별되는 이름을 사용하세요."); return; }
        DialogResult = DialogResult.OK; Close();
    }
    private void Render()
    {
        int scroll = -_cards.AutoScrollPosition.Y; _cards.SuspendLayout();
        foreach (Control old in _cards.Controls.Cast<Control>().ToList()) old.Dispose();
        foreach (SheetSetDefinition set in _editor.Sets)
        {
            var card = new Panel { Width = Math.Max(780, _cards.ClientSize.Width - 28), Height = 83 + set.SheetUniqueIds.Count * 32,
                Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BorderStyle = BorderStyle.FixedSingle, Tag = set.Id };
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
            header.Controls.Add(new Label { Text = set.SheetUniqueIds.Count > 1 ? $"세트 · {set.SheetUniqueIds.Count}장" : "시트 · 1장", Width = 90, Margin = new Padding(0, 7, 8, 0) });
            var name = new TextBox { Text = set.Name, Width = 215 }; name.TextChanged += (_, _) => set.Name = name.Text;
            header.Controls.Add(name);
            var direction = new ComboBox { Width = 104, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(14, 3, 3, 3) };
            direction.Items.AddRange(new object[] { "가로 일렬", "세로 일렬" }); direction.SelectedIndex = set.Direction == "Vertical" ? 1 : 0;
            direction.SelectedIndexChanged += (_, _) => set.Direction = direction.SelectedIndex == 1 ? "Vertical" : "Horizontal"; header.Controls.Add(direction);
            header.Controls.Add(new Label { Text = $"간격 {set.MarginMm:N0} mm", AutoSize = true, Margin = new Padding(12, 7, 2, 0) });
            var members = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            for (int i = 0; i < set.SheetUniqueIds.Count; i++)
            {
                int index = i; string id = set.SheetUniqueIds[i]; bool exists = _sheets.TryGetValue(id, out var sheet);
                var row = new FlowLayoutPanel { Width = 740, Height = 30, Margin = Padding.Empty, WrapContents = false };
                row.Controls.Add(new Label { Text = $"{i + 1:00}   " + (exists ? $"{sheet!.Number}   {sheet.Name}" : "[프로젝트에 없는 시트]"),
                    Width = 600, ForeColor = exists ? UiTheme.Navy : Color.Firebrick, AutoEllipsis = true, Margin = new Padding(8, 6, 4, 0) });
                var up = new Button { Text = "↑", Width = 32, Height = 26, Enabled = i > 0, AccessibleName = "시트 위로" };
                var down = new Button { Text = "↓", Width = 32, Height = 26, Enabled = i < set.SheetUniqueIds.Count - 1, AccessibleName = "시트 아래로" };
                up.Click += (_, _) => Act(() => _editor.MoveSheet(set.Id, index, -1)); down.Click += (_, _) => Act(() => _editor.MoveSheet(set.Id, index, 1));
                row.Controls.Add(up); row.Controls.Add(down);
                if (!exists)
                {
                    var remove = new Button { Text = "×", Width = 28, Height = 26 };
                    remove.Click += (_, _) => Act(() => { set.SheetUniqueIds.RemoveAt(index); if (set.SheetUniqueIds.Count == 0) _editor.Sets.Remove(set); }); row.Controls.Add(remove);
                }
                members.Controls.Add(row);
            }
            card.Controls.Add(members); card.Controls.Add(header); _cards.Controls.Add(card); AttachSelection(card, set.Id);
        }
        ResizeCards(); RefreshSelection(); _cards.ResumeLayout(true); _cards.AutoScrollPosition = new Point(0, scroll);
    }
    private void AttachSelection(Control control, string id)
    {
        if (control is not Button)
            control.MouseDown += (_, e) => { if (e.Button != MouseButtons.Left) return;
                _editor.Select(id, ModifierKeys.HasFlag(Keys.Control), ModifierKeys.HasFlag(Keys.Shift)); RefreshSelection(); };
        foreach (Control child in control.Controls) AttachSelection(child, id);
    }
    private void RefreshSelection()
    {
        foreach (Panel card in _cards.Controls)
            card.BackColor = _editor.SelectedIds.Contains((string)card.Tag!) ? Color.FromArgb(216, 234, 251) : Color.White;
        _status.Text = $"선택 {_editor.SelectedIds.Count}개 · 전체 {_editor.Sets.Count}세트 · 순서는 ↑↓ 버튼으로 변경 · 간격 변경: 창Export 탭 → 설정";
    }
    private void ResizeCards()
    { foreach (Control card in _cards.Controls) card.Width = Math.Max(780, _cards.ClientSize.Width - 28); }
}
