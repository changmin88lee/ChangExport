using System.Reflection;
using System.Windows.Forms;
using ChangExport.Models;
using ChangExport.UI;

internal static class SheetGroupUiRegression
{
    internal static void Run(string output, Action<bool, string> check, Action<Form, string> render)
    {
        var host = Enumerable.Range(1, 3).Select(index => new SheetDescriptor($"host-{index}", index,
            $"A-{index:000}", $"호스트 시트 {index}", "", "통합모델", true)).ToArray();
        var architecture = Enumerable.Range(1, 300).Select(index => new SheetDescriptor($"architecture-{index}", 1000 + index,
            $"A-{index:000}", $"건축 링크 시트 {index}", "link:architecture", "건축모델", false, @"C:\Models\Architecture.rvt", 1)).ToArray();
        var structure = Enumerable.Range(1, 200).Select(index => new SheetDescriptor($"structure-{index}", 2000 + index,
            $"S-{index:000}", $"구조 링크 시트 {index}", "link:structure", "구조모델", false, @"C:\Models\Structure.rvt", 1)).ToArray();
        var sheets = host.Concat(architecture).Concat(structure).ToArray();
        var sets = sheets.Select(sheet => new SheetSetDefinition { Name = sheet.Number, TemplateId = "template", SheetUniqueIds = new() { sheet.Key } }).ToArray();
        var assignments = sheets.ToDictionary(sheet => sheet.Key, _ => "template");
        using var form = new SheetGroupManagerForm(sheets, sets, new[] { new LayerTemplateChoice("template", "실무 레이어") }, assignments);
        var tree = Controls(form).OfType<TreeView>().Single(tree => tree.AccessibleName == "모델별 시트 목록");
        check(tree.Nodes.Count == 3 && tree.Nodes[0].Text.StartsWith("현재 프로젝트")
            && tree.Nodes[1].Text.StartsWith("링크 1 · 건축모델") && tree.Nodes[2].Text.StartsWith("링크 2 · 구조모델"),
            "Sheet browser starts with host then separately numbered linked models");
        check(tree.Nodes[0].IsExpanded && !tree.Nodes[1].IsExpanded && !tree.Nodes[2].IsExpanded,
            "Host starts expanded while large linked-model groups stay collapsed");
        check(Controls(form).Count() < 80, "Hundreds of linked sheets do not create one WinForms control per sheet");
        render(form, Path.Combine(output, "sets-linked-models.png"));
        TreeNode originalHostNode = tree.Nodes[0];
        typeof(SheetGroupManagerForm).GetMethod("PopulateSourceNode", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, new object[] { tree.Nodes[0] });
        typeof(SheetGroupManagerForm).GetMethod("SelectDetails", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, new object[] { tree.Nodes[0].Nodes[0] });
        var detailName = Controls(form).OfType<TextBox>().Single(box => box.AccessibleName == "선택 시트 세트 이름");
        detailName.Text = "호스트 시트 이름 수정";
        check(ReferenceEquals(originalHostNode, tree.Nodes[0]) && form.ResultSets.Any(set => set.Name == "호스트 시트 이름 수정"),
            "Editing a sheet updates only its visible node without rebuilding the source tree");
        typeof(SheetGroupManagerForm).GetMethod("PopulateSourceNode", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, new object[] { tree.Nodes[1] });
        tree.Nodes[1].Expand();
        check(tree.Nodes[1].Nodes.Count == 300, "Expanding one linked model reveals only its cached sheet nodes");

        typeof(SheetGroupManagerForm).GetMethod("MoveSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, new object[] { "host", "link:structure", true });
        check(form.ResultSourceOrder.SequenceEqual(new[] { "link:architecture", "link:structure", "host" }),
            "Host model can be reordered after linked models");
        typeof(SheetGroupManagerForm).GetMethod("BuildTree", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, new object?[] { null });
        check(tree.Nodes[0].Text.StartsWith("링크 1 · 건축모델") && tree.Nodes[1].Text.StartsWith("링크 2 · 구조모델")
            && tree.Nodes[2].Text.StartsWith("현재 프로젝트"), "Source drag order is reflected without rebuilding per-sheet controls");
    }

    private static IEnumerable<Control> Controls(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Controls(child)) yield return nested; } }
}
