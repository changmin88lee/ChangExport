using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace ChangExport.UI;

public sealed class SheetGroupRow
{
    public long ElementId { get; init; }
    public string SheetNumber { get; init; } = string.Empty;
    public string SheetName { get; init; } = string.Empty;
    public string ExportGroup { get; set; } = string.Empty;
    public int ExportOrder { get; set; }
    public string FilePreview => Sanitize(ExportGroup.Length == 0 ? "미지정" : ExportGroup) + ".dwg";

    private static string Sanitize(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value;
    }
}

public sealed class SheetGroupManagerForm : Form
{
    private readonly BindingList<SheetGroupRow> _rows;
    private readonly DataGridView _grid;
    private readonly TextBox _groupBox;
    public IReadOnlyList<SheetGroupRow> Rows => _rows.ToList();

    public SheetGroupManagerForm(IEnumerable<SheetGroupRow> rows)
    {
        _rows = new BindingList<SheetGroupRow>(rows.OrderBy(x => x.SheetNumber, StringComparer.CurrentCultureIgnoreCase).ToList());
        Text = "Sheet 그룹 관리";
        Width = 940;
        Height = 620;
        MinimumSize = new Size(760, 500);
        UiTheme.Apply(this);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        title.Controls.Add(UiTheme.Heading("Sheet 그룹 및 출력 순서"));
        title.Controls.Add(UiTheme.Muted("같은 그룹의 시트는 후처리 엔진 연결 시 하나의 최종 DWG로 묶입니다. Beta는 그룹별 폴더에 Native DWG를 생성합니다."));
        root.Controls.Add(title);

        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 8) };
        toolbar.Controls.Add(new Label { Text = "선택 행 그룹", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        _groupBox = new TextBox { Width = 180, PlaceholderText = "예: 평면도" };
        toolbar.Controls.Add(_groupBox);
        Button assign = UiTheme.SecondaryButton("그룹 일괄 지정");
        assign.Click += (_, _) => AssignGroup();
        toolbar.Controls.Add(assign);
        Button order = UiTheme.SecondaryButton("그룹별 Order 자동 번호");
        order.Click += (_, _) => AutoOrder();
        toolbar.Controls.Add(order);
        root.Controls.Add(toolbar);

        _grid = UiTheme.Grid();
        _grid.AutoGenerateColumns = false;
        _grid.DataSource = _rows;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SheetGroupRow.SheetNumber), HeaderText = "Sheet 번호", ReadOnly = true, FillWeight = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SheetGroupRow.SheetName), HeaderText = "Sheet 이름", ReadOnly = true, FillWeight = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SheetGroupRow.ExportGroup), HeaderText = "CAD_EXPORT_GROUP", FillWeight = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SheetGroupRow.ExportOrder), HeaderText = "CAD_EXPORT_ORDER", FillWeight = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(SheetGroupRow.FilePreview), HeaderText = "최종 파일명 미리보기", ReadOnly = true, FillWeight = 110 });
        root.Controls.Add(_grid);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        Button save = UiTheme.PrimaryButton("프로젝트에 저장");
        save.Click += (_, _) => { _grid.EndEdit(); DialogResult = DialogResult.OK; Close(); };
        Button cancel = UiTheme.SecondaryButton("취소");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void AssignGroup()
    {
        string group = _groupBox.Text.Trim();
        IEnumerable<DataGridViewRow> selected = _grid.SelectedRows.Cast<DataGridViewRow>();
        foreach (DataGridViewRow row in selected)
        {
            if (row.DataBoundItem is SheetGroupRow item) item.ExportGroup = group;
        }
        _grid.Refresh();
    }

    private void AutoOrder()
    {
        foreach (IGrouping<string, SheetGroupRow> group in _rows.GroupBy(x => x.ExportGroup ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            int order = 1;
            foreach (SheetGroupRow row in group.OrderBy(x => x.SheetNumber, StringComparer.CurrentCultureIgnoreCase))
                row.ExportOrder = order++;
        }
        _grid.Refresh();
    }
}
