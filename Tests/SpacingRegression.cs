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
        check(config.SchemaVersion == 6 && config.SheetSpacingMm == 0 && config.SheetSets.All(s => s.MarginMm == 0),
            "Legacy settings migrate to one global touching-sheet spacing");
        check(File.ReadAllText(path) == legacy && !File.Exists(path + ".bak"), "Loading does not rewrite the user profile");
        check(config.SelectedSetup == "기존 설정" && config.Setups[0].Layers[0].Layer == "S-WALL"
            && config.SheetSets[1].Direction == "Vertical" && config.SheetSets[0].SheetUniqueIds.SequenceEqual(new[] { "a", "b" }),
            "Migration preserves layers, membership, order and direction");

        var schema4 = new RevitExportConfiguration { SchemaVersion = 4, SheetSets = new()
        {
            new() { Name = "기존 첫 세트", MarginMm = 1250 }, new() { Name = "기존 둘째 세트", MarginMm = 500 }
        } };
        string schema4Path = Path.Combine(output, "schema4-spacing.json"); File.WriteAllText(schema4Path, JsonSerializer.Serialize(schema4));
        var migrated4 = new ExportConfigurationStore(schema4Path).Load();
        check(migrated4.SchemaVersion == 6 && migrated4.SheetSpacingMm == 1250 && migrated4.SheetSets.All(s => s.MarginMm == 0),
            "Schema 4 converts the first persisted set spacing to the new global value and retires per-set values");

        config.SheetSpacingMm = 750; store.Save(config); var saved = store.Load();
        check(saved.SheetSpacingMm == 750 && saved.SheetSets.All(s => s.MarginMm == 0),
            "Global sheet spacing survives reload without per-set spacing");
        check(File.ReadAllText(path + ".bak") == legacy, "First explicit save backs up the original profile");

        var editor = new SheetSetEditor(new[] { new SheetSetDefinition { Id = "a", TemplateId = "template", SheetUniqueIds = new() { "a" } },
            new SheetSetDefinition { Id = "b", TemplateId = "template", SheetUniqueIds = new() { "b" } } });
        editor.Select("a", false, false); editor.Select("b", true, false); editor.Combine("신규 세트");
        check(editor.Sets.Single().TemplateId == "template" && editor.Sets.Single().SheetUniqueIds.SequenceEqual(new[] { "a", "b" }),
            "New combined sets preserve the shared template and sheet order");

        var unassigned = new SheetSetEditor(new[] { new SheetSetDefinition { Id = "u1", SheetUniqueIds = new() { "u1" } },
            new SheetSetDefinition { Id = "u2", SheetUniqueIds = new() { "u2" } } });
        unassigned.Select("u1", false, false); unassigned.Select("u2", true, false); unassigned.Combine("미지정 세트");
        check(unassigned.Sets.Single().TemplateId.Length == 0 && unassigned.Sets.Single().SheetUniqueIds.SequenceEqual(new[] { "u1", "u2" }),
            "Unassigned sheets can be combined without assigning a template first");
        var unassignedSet = unassigned.Sets.Single(); string setId = unassignedSet.Id;
        var assignments = new Dictionary<string, string>();
        unassigned.AssignTemplate(setId, "template", assignments);
        check(assignments.Count == 2 && assignments.Values.All(id => id == "template") && unassigned.Sets.Single().Id == setId,
            "Changing a set template updates every member assignment without rebuilding the set");
        unassigned.AssignTemplate(setId, string.Empty, assignments);
        check(assignments.Count == 0 && unassigned.Sets.Single().Id == setId
            && unassigned.Sets.Single().SheetUniqueIds.SequenceEqual(new[] { "u1", "u2" }),
            "Selecting unassigned clears member assignments but preserves set membership");

        var mixed = new SheetSetEditor(new[] { new SheetSetDefinition { Id = "a", TemplateId = "architecture", SheetUniqueIds = new() { "a" } },
            new SheetSetDefinition { Id = "b", TemplateId = "structure", SheetUniqueIds = new() { "b" } } });
        mixed.Select("a", false, false); mixed.Select("b", true, false);
        try { mixed.Combine("금지"); check(false, "Different-template sheets cannot be combined"); }
        catch (InvalidOperationException) { check(true, "Different-template sheets cannot be combined"); }

        var assignedConfig = new RevitExportConfiguration();
        SheetSetService.ApplyAssignments(assignedConfig, editor.Sets, new Dictionary<string, string>());
        check(assignedConfig.SheetSets.Single().TemplateId == "template"
            && assignedConfig.SheetTemplateIds.Count == 2 && assignedConfig.SheetTemplateIds.Values.All(id => id == "template"),
            "Set template is authoritative and save synchronizes every member assignment");

        int saves = 0;
        void Save(string keyword, double spacing)
        { saves++; config.WideLineKeyword = keyword; config.SheetSpacingMm = spacing; store.Save(config); }
        using var dialog = new ChangExportSettingsForm("204동 건축 도면", saved.WideLineKeyword, saved.SheetSpacingMm, Save);
        render(dialog, Path.Combine(output, "settings.png"));
        var input = Descendants(dialog).OfType<NumericUpDown>().Single();
        check(!Descendants(dialog).OfType<ComboBox>().Any() && !Descendants(dialog).OfType<DataGridView>().Any(),
            "Global spacing settings do not expose a target-set selector");
        check(!Descendants(dialog).OfType<Button>().Any(b => b.Text.Contains("패밀리 블록", StringComparison.Ordinal)), "Manual family block selection is removed");
        check(dialog.WideLineKeyword == "##" && dialog.SheetSpacingMm == 750, "Existing keyword and global spacing load in the settings window");
        Descendants(dialog).OfType<TextBox>().Single(t => t.AccessibleName == "전역폭 판별 문자열").Text = "  전역폭  ";
        input.Value = 1250;
        check(dialog.WideLineKeyword == "전역폭" && dialog.SheetSpacingMm == 1250, "Global settings edits are returned without set selection");
        var reset = Descendants(dialog).OfType<LinkLabel>().Single();
        typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(reset, new object[] { new LinkLabelLinkClickedEventArgs(reset.Links[0]) });
        check(dialog.SheetSpacingMm == 0 && input.Value == 0, "Reset restores the one common spacing to touching sheets");
        dialog.Size = dialog.MinimumSize; render(dialog, Path.Combine(output, "settings-small.png")); CheckBounds(dialog, check);
        Click(dialog, "취소");
        check(dialog.DialogResult == DialogResult.Cancel && saves == 0 && store.Load().SheetSpacingMm == 750,
            "Cancel does not write either keyword or global spacing");

        using var apply = new ChangExportSettingsForm("204동 건축 도면", "방수##", saved.SheetSpacingMm, Save);
        render(apply, Path.Combine(output, "settings-save.png"));
        Descendants(apply).OfType<NumericUpDown>().Single().Value = 2500; Click(apply, "설정 저장");
        check(apply.DialogResult == DialogResult.OK && apply.SheetSpacingMm == 2500, "Apply returns the edited global spacing");
        check(saves == 1 && store.Load().WideLineKeyword == "방수##" && store.Load().SheetSpacingMm == 2500,
            "Settings save persists keyword and global spacing without exporting");
        check(store.Load().Setups[0].Layers[0].Layer == "S-WALL" && store.Load().SheetSets[1].Direction == "Vertical",
            "Settings save preserves layers, set membership and direction");
        using var scaled = new ChangExportSettingsForm("긴 프로젝트 이름 · 부천역곡 204동 건축 실시설계 도면 출력", "##", 2500, Save);
        scaled.Scale(new System.Drawing.SizeF(1.5f, 1.5f));
        render(scaled, Path.Combine(output, "settings-150.png")); CheckBounds(scaled, check);
        using var preview = new ChangExportSettingsForm("시트가 없는 프로젝트", "", 0, (_, _) => { });
        render(preview, Path.Combine(output, "settings-preview.png"));
        check(Descendants(preview).OfType<NumericUpDown>().Single().Enabled, "Global spacing remains editable independently of existing sets");
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
