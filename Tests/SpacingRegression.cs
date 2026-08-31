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

        int saves = 0;
        void Save(string keyword, IReadOnlyList<SheetSetDefinition> sets)
        { saves++; config.WideLineKeyword = keyword; config.SheetSets = sets.Select(s => s.Copy()).ToList(); store.Save(config); }
        using var dialog = new ChangExportSettingsForm("204동 건축 도면", saved.WideLineKeyword, saved.SheetSets, Save);
        render(dialog, Path.Combine(output, "settings.png"));
        var selection = Descendants(dialog).OfType<ComboBox>().Single();
        var input = Descendants(dialog).OfType<NumericUpDown>().Single();
        check(!Descendants(dialog).OfType<DataGridView>().Any(), "Settings use dedicated inputs without a spreadsheet");
        check(dialog.WideLineKeyword == "##", "Existing keyword loads in the separate settings window");
        Descendants(dialog).OfType<TextBox>().Single(t => t.AccessibleName == "전역폭 판별 문자열").Text = "  전역폭  ";
        check(dialog.WideLineKeyword == "전역폭", "Keyword edits are trimmed in the separate settings window");
        selection.SelectedIndex = 0; input.Value = 1250;
        selection.SelectedIndex = 1;
        check(input.Value == 750, "Selecting another set loads its own spacing"); input.Value = 500;
        check(dialog.ResultSets[0].MarginMm == 1250 && dialog.ResultSets[1].MarginMm == 500, "Spacing edits apply only to the selected set");
        check(saved.SheetSets[0].MarginMm == 10000 && saved.SheetSets[1].MarginMm == 750, "Draft changes do not mutate the caller before Apply");
        var reset = Descendants(dialog).OfType<LinkLabel>().Single();
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reset, new object[] { new LinkLabelLinkClickedEventArgs(reset.Links[0]) });
        check(dialog.ResultSets.All(s => s.MarginMm == 0) && input.Value == 0, "Reset restores touching sheets in both directions");
        dialog.Size = dialog.MinimumSize; render(dialog, Path.Combine(output, "settings-small.png"));
        CheckBounds(dialog, check);
        Click(dialog, "취소");
        check(dialog.DialogResult == DialogResult.Cancel && saves == 0 && store.Load().WideLineKeyword == "##"
            && store.Load().SheetSets[0].MarginMm == 10000, "Cancel does not write either keyword or spacing");

        using var apply = new ChangExportSettingsForm("204동 건축 도면", "방수##", saved.SheetSets, Save);
        render(apply, Path.Combine(output, "settings-save.png"));
        Descendants(apply).OfType<NumericUpDown>().Single().Value = 2500; Click(apply, "설정 저장");
        check(apply.DialogResult == DialogResult.OK && apply.ResultSets[0].MarginMm == 2500, "Apply returns the edited spacing");
        check(saves == 1 && store.Load().WideLineKeyword == "방수##" && store.Load().SheetSets[0].MarginMm == 2500
            && store.Load().SheetSets[1].MarginMm == 750, "Settings save persists keyword and per-set spacing without exporting");
        check(store.Load().Setups[0].Layers[0].Layer == "S-WALL" && store.Load().SheetSets[1].Direction == "Vertical",
            "Settings save preserves layers, set membership and direction");
        using var scaled = new ChangExportSettingsForm("긴 프로젝트 이름 · 부천역곡 204동 건축 실시설계 도면 출력", "##", saved.SheetSets, Save);
        scaled.Scale(new System.Drawing.SizeF(1.5f, 1.5f));
        render(scaled, Path.Combine(output, "settings-150.png")); CheckBounds(scaled, check);
        using var empty = new ChangExportSettingsForm("시트가 없는 프로젝트", "", Array.Empty<SheetSetDefinition>(), Save);
        render(empty, Path.Combine(output, "settings-empty.png"));
        check(!Descendants(empty).OfType<ComboBox>().Single().Enabled && !Descendants(empty).OfType<NumericUpDown>().Single().Enabled,
            "No-sheet settings disable only the spacing controls");
        check(empty.WideLineKeyword == "", "Disabled wide-line conversion remains empty");
        using var preview = new ChangExportSettingsForm("204동 건축 도면", "##", new[] {
            new SheetSetDefinition { Name = "AA-440", SheetUniqueIds = new() { "AA-441", "AA-442" } } }, (_, _) => { });
        render(preview, Path.Combine(output, "settings-preview.png"));
    }
    private static void CheckBounds(Form form, Action<bool, string> check)
    {
        foreach (var control in Descendants(form).Where(c => c is Button or Label or ComboBox or NumericUpDown || c is TextBox && c.AccessibleName == "전역폭 판별 문자열"))
        {
            var origin = form.PointToClient(control.Parent!.PointToScreen(control.Location));
            check(form.ClientRectangle.Contains(new System.Drawing.Rectangle(origin, control.Size)), "Settings control fits window: " + control.Text);
            for (var parent = control.Parent; parent != null && parent != form; parent = parent.Parent)
            {
                var relative = parent.PointToClient(control.Parent.PointToScreen(control.Location));
                check(parent.ClientRectangle.Contains(new System.Drawing.Rectangle(relative, control.Size)), "Settings control not clipped by a section: " + control.Text);
            }
        }
    }
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Click(Form form, string text) => typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(Descendants(form).OfType<Button>().Single(b => b.Text == text), new object[] { EventArgs.Empty });
}
