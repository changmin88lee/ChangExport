using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using ChangExport.Models;
using ChangExport.Standards;
using ChangExport.UI;

internal static class SpacingRegression
{
    public static void Run(string output, Action<bool, string> check, Action<Form, string> render)
    {
        var source = new RevitExportConfiguration { SchemaVersion = 1, SelectedSetup = "기존 설정",
            Setups = new() { new ExportSetupEdits { SetupName = "기존 설정", Layers = new() { new RevitLayerRow { Category = "벽", Layer = "S-WALL" } } } },
            SheetSets = new() {
                new SheetSetDefinition { Id = "horizontal", Name = "가로 세트", SheetUniqueIds = new() { "a", "b" }, MarginMm = 10000 },
                new SheetSetDefinition { Id = "vertical", Name = "세로 세트", SheetUniqueIds = new() { "c", "d" }, Direction = "Vertical", MarginMm = 500 } } };
        string path = Path.Combine(output, "legacy-spacing.json"), legacy = JsonSerializer.Serialize(source);
        File.WriteAllText(path, legacy);
        var store = new ExportConfigurationStore(path); var config = store.Load();
        check(config.SchemaVersion == 2 && config.SheetSets.All(s => s.MarginMm == 0), "Existing horizontal and vertical sets start at zero");
        check(File.ReadAllText(path) == legacy && !File.Exists(path + ".bak"), "Loading does not rewrite the user profile");
        check(config.SelectedSetup == "기존 설정" && config.Setups[0].Layers[0].Layer == "S-WALL"
            && config.SheetSets[1].Direction == "Vertical" && config.SheetSets[0].SheetUniqueIds.SequenceEqual(new[] { "a", "b" }),
            "Migration preserves layers, membership, order and direction");
        config.SheetSets[0].MarginMm = 10000; config.SheetSets[1].MarginMm = 750;
        store.Save(config); var saved = store.Load();
        check(saved.SheetSets[0].MarginMm == 10000 && saved.SheetSets[1].MarginMm == 750, "Explicit new spacing survives reload, including old default value");
        check(File.ReadAllText(path + ".bak") == legacy, "First explicit save backs up the original profile");
        var editor = new SheetSetEditor(new[] { new SheetSetDefinition { Id = "a", SheetUniqueIds = new() { "a" } },
            new SheetSetDefinition { Id = "b", SheetUniqueIds = new() { "b" } } });
        editor.Select("a", false, false); editor.Select("b", true, false); editor.Combine("신규 세트");
        check(editor.Sets.Single().MarginMm == 0, "New combined sets use zero spacing");

        using var dialog = new SheetSpacingSettingsForm(saved.SheetSets);
        render(dialog, Path.Combine(output, "spacing.png"));
        var grid = Descendants(dialog).OfType<DataGridView>().Single();
        var input = Descendants(dialog).OfType<NumericUpDown>().Single();
        check(!grid.AllowUserToResizeColumns && !grid.AllowUserToResizeRows && grid.ReadOnly, "Spacing table cannot be dragged or edited directly");
        grid.CurrentCell = grid.Rows[0].Cells[0]; input.Value = 1250;
        grid.CurrentCell = grid.Rows[1].Cells[0];
        check(input.Value == 750, "Selecting another set loads its own spacing"); input.Value = 500;
        check(dialog.ResultSets[0].MarginMm == 1250 && dialog.ResultSets[1].MarginMm == 500, "Spacing edits apply only to the selected set");
        check(saved.SheetSets[0].MarginMm == 10000 && saved.SheetSets[1].MarginMm == 750, "Draft changes do not mutate the caller before Apply");
        Click(dialog, "전체 간격 0");
        check(dialog.ResultSets.All(s => s.MarginMm == 0) && input.Value == 0, "Reset restores touching sheets in both directions");
        dialog.Size = dialog.MinimumSize; render(dialog, Path.Combine(output, "spacing-small.png"));
        Click(dialog, "취소");
        check(dialog.DialogResult == DialogResult.Cancel && saved.SheetSets[0].MarginMm == 10000, "Cancel leaves saved values unchanged");

        using var apply = new SheetSpacingSettingsForm(saved.SheetSets);
        render(apply, Path.Combine(output, "spacing-apply.png"));
        Descendants(apply).OfType<NumericUpDown>().Single().Value = 2500; Click(apply, "적용");
        check(apply.DialogResult == DialogResult.OK && apply.ResultSets[0].MarginMm == 2500, "Apply returns the edited spacing");
        config.SheetSets = apply.ResultSets.ToList(); store.Save(config);
        check(store.Load().SheetSets[0].MarginMm == 2500, "Dialog value survives explicit save and reload");
    }
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Click(Form form, string text) => typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(Descendants(form).OfType<Button>().Single(b => b.Text == text), new object[] { EventArgs.Empty });
}
