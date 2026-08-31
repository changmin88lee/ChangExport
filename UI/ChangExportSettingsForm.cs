using System.Drawing;
using System.Windows.Forms;
using ChangExport.Models;

namespace ChangExport.UI;

public sealed class ChangExportSettingsForm : Form
{
    private readonly List<SheetSetDefinition> _sets;
    private readonly TextBox _keyword;
    private readonly ComboBox _set;
    private readonly NumericUpDown _spacing;
    private readonly Label _setDescription;
    private readonly Action<string, IReadOnlyList<SheetSetDefinition>> _save;
    private bool _selecting;
    public string WideLineKeyword => _keyword.Text.Trim();
    public IReadOnlyList<SheetSetDefinition> ResultSets => _sets.Select(s => s.Copy()).ToList();

    public ChangExportSettingsForm(string projectName, string keyword, IEnumerable<SheetSetDefinition> sets,
        Action<string, IReadOnlyList<SheetSetDefinition>> save)
    {
        _sets = sets.Select(s => s.Copy()).ToList(); _save = save;
        UiTheme.Apply(this);
        Text = "창Export 설정"; ClientSize = new Size(700, 640); MinimumSize = new Size(640, 660);
        MaximizeBox = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);

        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 20) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.Controls.Add(UiTheme.Heading("설정"));
        heading.Controls.Add(new Label { Text = "현재 프로젝트 · " + projectName, AutoEllipsis = true, Dock = DockStyle.Top,
            Height = 24, ForeColor = Color.FromArgb(95, 103, 115), Margin = Padding.Empty }); root.Controls.Add(heading);

        var lines = Section("선 전역폭", "선 스타일 이름에 아래 문자열이 포함되면 전역폭 폴리선으로 출력합니다.");
        lines.Margin = new Padding(0, 0, 0, 16);
        lines.Controls.Add(FieldLabel("판별 문자열"), 0, 2);
        _keyword = new TextBox { Text = keyword, MaxLength = 100, Width = 220, Anchor = AnchorStyles.Left,
            AccessibleName = "전역폭 판별 문자열", Margin = new Padding(0, 4, 0, 4) };
        lines.Controls.Add(_keyword, 1, 2);
        var lineHint = Hint("두께는 Revit 출력 선굵기와 시트 축척을 따릅니다. 빈 값이면 변환하지 않습니다.");
        lines.Controls.Add(lineHint, 0, 3); lines.SetColumnSpan(lineHint, 2); root.Controls.Add(lines);

        var placement = Section("시트 배치 간격", "가로·세로 모두 축척 적용 후 모형공간 mm 기준입니다.");
        placement.Dock = DockStyle.Fill;
        placement.Controls.Add(FieldLabel("대상 세트"), 0, 2);
        _set = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "간격을 변경할 세트", Margin = new Padding(0, 4, 0, 4), DisplayMember = nameof(SheetSetDefinition.Name) };
        foreach (var item in _sets) _set.Items.Add(item);
        placement.Controls.Add(_set, 1, 2);
        _setDescription = Hint(""); placement.Controls.Add(_setDescription, 1, 3);
        placement.Controls.Add(FieldLabel("시트 사이 간격"), 0, 4);
        var value = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _spacing = new NumericUpDown { Minimum = 0, Maximum = 100000, DecimalPlaces = 0, ThousandsSeparator = true,
            Width = 130, AccessibleName = "선택한 세트 간격 mm", Margin = new Padding(0, 4, 8, 4) };
        value.Controls.Add(_spacing); value.Controls.Add(new Label { Text = "mm", AutoSize = true, Margin = new Padding(0, 7, 0, 0) });
        placement.Controls.Add(value, 1, 4);
        var gapHint = Hint("0 mm이면 시트 바깥 경계를 붙입니다. 도곽 안쪽 여백은 유지됩니다.");
        placement.Controls.Add(gapHint, 0, 5); placement.SetColumnSpan(gapHint, 2);
        var reset = new LinkLabel { Text = "모든 세트 간격을 0 mm로", AutoSize = true, LinkColor = UiTheme.Blue,
            ActiveLinkColor = UiTheme.Navy, Margin = new Padding(0, 10, 0, 0), Enabled = _sets.Count > 0 };
        reset.LinkClicked += (_, _) => { foreach (var item in _sets) item.MarginMm = 0; SelectSet(); };
        placement.Controls.Add(reset, 0, 6); placement.SetColumnSpan(reset, 2); root.Controls.Add(placement);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 20, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var scope = Hint("현재 프로젝트에만 저장됩니다."); scope.Anchor = AnchorStyles.Left; footer.Controls.Add(scope, 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var cancel = UiTheme.SecondaryButton("취소"); cancel.DialogResult = DialogResult.Cancel;
        var commit = UiTheme.PrimaryButton("설정 저장"); commit.Click += (_, _) => Save();
        actions.Controls.Add(cancel); actions.Controls.Add(commit); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer);
        AcceptButton = commit; CancelButton = cancel;
        _set.SelectedIndexChanged += (_, _) => SelectSet();
        _spacing.ValueChanged += (_, _) => { if (!_selecting && _set.SelectedItem is SheetSetDefinition item) item.MarginMm = (double)_spacing.Value; };
        _set.Enabled = _sets.Count > 0;
        if (_sets.Count > 0) _set.SelectedIndex = 0; else SelectSet();
    }

    private static TableLayoutPanel Section(string title, string description)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White,
            Padding = new Padding(20, 16, 20, 16), ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var label = new Label { Text = title, AutoSize = true, Font = new Font("맑은 고딕", 11F, FontStyle.Bold),
            ForeColor = UiTheme.Navy, Margin = new Padding(0, 0, 0, 6) };
        panel.Controls.Add(label, 0, 0); panel.SetColumnSpan(label, 2);
        var hint = Hint(description); hint.Margin = new Padding(0, 0, 0, 12);
        panel.Controls.Add(hint, 0, 1); panel.SetColumnSpan(hint, 2);
        return panel;
    }
    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        ForeColor = UiTheme.Navy, Margin = new Padding(0, 7, 12, 7) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(95, 103, 115), Margin = new Padding(0, 5, 0, 0) };

    private void SelectSet()
    {
        _selecting = true;
        try
        {
            var item = _set.SelectedItem as SheetSetDefinition;
            _spacing.Enabled = item != null; _spacing.Value = (decimal)Math.Clamp(item?.MarginMm ?? 0, 0, 100000);
            _setDescription.Text = item == null ? "시트 세트 구성에서 세트를 먼저 만들어 주세요."
                : $"{(item.Direction == "Vertical" ? "세로" : "가로")} 일렬 · {item.SheetUniqueIds.Count}장"
                    + (item.SheetUniqueIds.Count < 2 ? " · 두 장 이상 묶으면 간격이 적용됩니다." : "");
        }
        finally { _selecting = false; }
    }
    private void Save()
    {
        ValidateChildren();
        try { _save(WideLineKeyword, ResultSets); DialogResult = DialogResult.OK; Close(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "설정 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
