using ChangExport.Models;
using ChangExport.Standards;

internal static class OutputSetupRegression
{
    public static void Run(string output, Action<bool, string> check)
    {
        var catalog = new[] { RevitCategoryCatalog.DefaultRow("벽", "", "Model"), RevitCategoryCatalog.DefaultRow("바닥", "", "Model"),
            RevitCategoryCatalog.DefaultRow("문자", "", "Annotation"), RevitCategoryCatalog.DefaultRow("치수", "", "Annotation"),
            RevitCategoryCatalog.DefaultRow("선", "상세선", "Annotation"), RevitCategoryCatalog.DefaultRow("채워진 영역", "", "Annotation") };
        catalog[0].CategoryId = -2000011;
        var config = new RevitExportConfiguration { SelectedSetup = "기존 Revit 설정", Setups = new() { new() { SetupName = "기존 Revit 설정" } } };
        check(RevitLayerMappingService.SetupNames(config).SequenceEqual(new[] { "" }), "Fresh setup list contains only ChangExport default, not old Revit presets");
        check(RevitLayerMappingService.MergeCatalog(catalog, Array.Empty<RevitLayerRow>()).Count == catalog.Length, "Default catalog exists without an export layer table or saved preset");
        check(catalog.All(r => r.Layer.Length > 0 && r.CutLayer.Length > 0 && r.Color == 7 && r.CutColor == 7 && r.SpecialType == -1),
            "All default model and 2D rows have explicit layers/colors and Revit Default special type");
        var wall = catalog[0].Copy(); wall.Layer = "S-WALL"; wall.CutLayer = "S-WALL-CUT"; wall.Color = wall.CutColor = 3;
        var rule1 = wall.Copy(); rule1.IsCustom = true; rule1.RuleId = "first"; rule1.TypeNameContains = "RC"; rule1.Layer = "S-RC"; rule1.CutLayer = "S-RC-CUT";
        var rule2 = rule1.Copy(); rule2.RuleId = "second"; rule2.TypeNameContains = "벽"; rule2.Layer = "S-SECOND";
        var file = new OutputSetupFile { Name = "구조 도면", Layers = RevitLayerMappingService.MergeCatalog(catalog, new[] { wall, rule1, rule2 }) };
        string path = Path.Combine(output, "portable-output.json"); file.Save(path); var loaded = OutputSetupFile.Load(path);
        check(loaded.Layers.Count == catalog.Length + 2 && loaded.Layers.Single(r => r.RuleId == "first").Layer == "S-RC", "Full catalog and custom rules roundtrip, not just deltas");
        var nextProject = catalog.Concat(new[] { RevitCategoryCatalog.DefaultRow("추가 카테고리", "", "Model") }).ToList();
        var merged = RevitLayerMappingService.MergeCatalog(nextProject, loaded.Layers);
        check(merged.Count == catalog.Length + 3 && merged.Any(r => r.Category == "추가 카테고리"), "Loading in another project preserves settings and supplements new categories");
        int parent = merged.FindIndex(r => r.Category == "벽" && !r.IsCustom);
        check(merged[parent + 1].RuleId == "first" && merged[parent + 2].RuleId == "second", "Custom filter order is retained beneath its category");
        var index = new TypeRuleIndex(merged);
        check(index.Match("벽", "rc 벽")?.RuleId == "first" && index.Match("벽", "rc 벽")?.RuleId == "first", "Cached filtering preserves first match and case-insensitive contains");
        check(index.Match("바닥", "RC") == null && index.Match("벽", "없는 유형") == null, "Filter cache isolates categories and negative results");
        var resetIndex = new TypeRuleIndex(new[] { rule2, rule1 });
        check(resetIndex.Match("벽", "RC 벽")?.RuleId == "second", "Rule cache is scoped to the current rule ordering");
        string original = File.ReadAllText(path);
        loaded.Layers[0].Color = 256;
        try { loaded.Save(path); check(false, "Invalid color rejected"); } catch (InvalidDataException) { check(true, "Invalid color rejected"); }
        check(File.ReadAllText(path) == original, "Failed validation never overwrites the previous profile");
        loaded = OutputSetupFile.Load(path); loaded.Layers[0].Layer = "수정 레이어"; loaded.Save(path);
        check(File.ReadAllText(path + ".bak") == original, "Explicit profile overwrite retains a backup");
        config.OutputSetups.Add(new ExportSetupEdits { SetupName = loaded.Name, Layers = loaded.Layers }); config.SelectedOutputSetup = loaded.Name;
        var store = new ExportConfigurationStore(Path.Combine(output, "independent-output-config.json")); store.Save(config); config = store.Load();
        check(RevitLayerMappingService.SetupNames(config).SequenceEqual(new[] { "", "구조 도면" }) && config.SelectedOutputSetup == "구조 도면", "Custom setup list and selection persist independently of Revit settings");
        check(config.Setups.Single().SetupName == "기존 Revit 설정", "Legacy profile data remains preserved");
    }
}
