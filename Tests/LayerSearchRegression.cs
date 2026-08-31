using System.Reflection;
using System.Windows.Forms;
using ChangExport.Models;
using ChangExport.Standards;
using ChangExport.UI;

internal static class LayerSearchRegression
{
    public static void Run(string output, Action<bool, string> check, Action<Form, string> render)
    {
        var rows = new List<RevitLayerRow>();
        foreach (string category in new[] { "바닥", "벽", "지붕", "지형 솔리드", "천장" })
        foreach (string sub in new[] { "", "구조", "마감", "하지재" }) rows.Add(RevitCategoryCatalog.DefaultRow(category, sub, "Model"));
        var store = new ExportConfigurationStore(Path.Combine(output, "search-settings.json"));
        using var form = new LayerRuleManagerForm(store, new(), new[] { "" }, _ => rows.Select(r => r.Copy()).ToList());
        T Field<T>(string name) => (T)typeof(LayerRuleManagerForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var grid = Field<DataGridView>("_grid"); var search = Field<TextBox>("_search");
        void Toggle(string category)
        {
            int row = grid.Rows.Cast<DataGridViewRow>().First(r => ((RevitLayerRow)r.DataBoundItem).Category == category).Index;
            typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(grid, new object[] { new DataGridViewCellEventArgs(0, row) });
        }
        render(form, Path.Combine(output, "search-collapsed.png"));
        check(grid.RowCount == 5, "Only category parents are initially visible");
        Toggle("벽"); check(grid.RowCount == 8, "+ reveals only the selected category's children");
        var wall = grid.Rows.Cast<DataGridViewRow>().Select(r => (RevitLayerRow)r.DataBoundItem).Single(r => r.Category == "벽" && r.Subcategory == "하지재");
        wall.Layer = "벽_하지재_수정";
        search.Text = "하지재";
        check(grid.RowCount == 5 && Field<Label>("_status").Text.Contains("검색 일치 5개"), "Search counts matching rows and shows only relevant parents before expansion");
        foreach (string category in new[] { "바닥", "벽", "지붕", "지형 솔리드", "천장" }) Toggle(category);
        check(grid.RowCount == 10 && grid.Rows.Cast<DataGridViewRow>().Select(r => (RevitLayerRow)r.DataBoundItem).All(r => r.Subcategory is "" or "하지재"),
            "Search expands matching children without unrelated siblings");
        render(form, Path.Combine(output, "search-matched.png"));
        search.Clear(); check(grid.RowCount == 8, "Clearing search restores normal expansion state");
        Toggle("벽"); check(grid.RowCount == 5, "Minus collapses the category");
        search.Text = "no-such-layer"; check(grid.RowCount == 0, "No match displays an empty list");
        search.Text = "하지재_수정"; Toggle("벽");
        check(grid.RowCount == 2, "Layer name search finds an unsaved edit even after collapse");
        typeof(LayerRuleManagerForm).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        var saved = store.Load().OutputSetups.Single().Layers;
        check(saved.Count == 20 && saved.Single(r => r.Category == "벽" && r.Subcategory == "하지재").Layer == wall.Layer,
            "Saving while filtered preserves all rows and edits");
    }
}
