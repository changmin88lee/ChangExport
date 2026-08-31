using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;

namespace ChangExport.UI;

public sealed class FamilyBlockSelectionForm : Form
{
    private readonly List<BlockFamilyChoice> _choices;
    private readonly HashSet<string> _selected;
    private readonly CheckedListBox _list;
    private readonly Label _count;
    private readonly TextBox _search;
    private bool _loading;
    public IReadOnlyList<string> SelectedIds => _selected.OrderBy(s => s, StringComparer.Ordinal).ToList();
    private sealed record Choice(BlockFamilyChoice Value)
    { public override string ToString() => Value.Name + "  ·  " + Value.Category; }

    public FamilyBlockSelectionForm(IEnumerable<BlockFamilyChoice> choices, IEnumerable<string> selected)
    {
        _choices = choices.DistinctBy(c => c.Identity).OrderBy(c => c.Category).ThenBy(c => c.Name).ToList();
        _selected = selected.ToHashSet(StringComparer.Ordinal);
        UiTheme.Apply(this); Text = "패밀리 블록 설정"; ClientSize = new Size(680, 580); MinimumSize = new Size(600, 540);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 100 : 0));
        Controls.Add(root);
        root.Controls.Add(UiTheme.Heading("추가 패밀리 선택"));
        root.Controls.Add(new Label { Text = "문·창·기둥·도곽은 기본으로 블록 처리합니다.\n엘리베이터·점자블록 등 다른 모형 패밀리는 아래에서 선택하세요.\n보·벽·바닥·기초·내부 작성 패밀리·독립 주석은 대상에서 제외됩니다.",
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(95, 103, 115), Margin = new Padding(0, 6, 0, 16) });
        _search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "패밀리 이름 / 카테고리 검색", AccessibleName = "추가 블록 패밀리 검색", Margin = new Padding(0, 0, 0, 10) };
        root.Controls.Add(_search);
        _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true,
            BorderStyle = BorderStyle.FixedSingle, AccessibleName = "추가 블록 패밀리 목록", Margin = Padding.Empty };
        root.Controls.Add(_list);
        _count = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(95, 103, 115), Margin = new Padding(0, 8, 0, 8) };
        root.Controls.Add(_count);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var apply = UiTheme.PrimaryButton("선택 적용"); apply.DialogResult = DialogResult.OK;
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(apply); footer.Controls.Add(cancel); root.Controls.Add(footer); AcceptButton = apply; CancelButton = cancel;
        _search.TextChanged += (_, _) => RefreshList();
        _list.ItemCheck += (_, e) =>
        {
            if (_loading || _list.Items[e.Index] is not Choice item) return;
            if (e.NewValue == CheckState.Checked) _selected.Add(item.Value.Identity); else _selected.Remove(item.Value.Identity);
            UpdateCount();
        };
        RefreshList();
    }

    private void RefreshList()
    {
        _loading = true; _list.BeginUpdate();
        try
        {
            _list.Items.Clear(); string text = _search.Text.Trim();
            foreach (var item in _choices.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Category.Contains(text, StringComparison.OrdinalIgnoreCase)))
                _list.Items.Add(new Choice(item), _selected.Contains(item.Identity));
        }
        finally { _list.EndUpdate(); _loading = false; }
        UpdateCount();
    }
    private void UpdateCount() => _count.Text = $"표시 {_list.Items.Count:N0}개 · 선택 {_choices.Count(c => _selected.Contains(c.Identity)):N0}개"
        + "\n선택 적용 후 이전 창의 ‘설정 저장’을 눌러야 저장됩니다.";
}
