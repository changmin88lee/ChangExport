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
        var architectureRows = loaded.Layers.Select(row => { var copy = row.Copy(); copy.ViewScope = ViewLayerScope.ArchitecturePlan; return copy; }).ToList();
        var structuralRows = loaded.Layers.Select(row => { var copy = row.Copy(); copy.ViewScope = ViewLayerScope.StructuralPlan;
            if (copy.Category == "벽" && !copy.IsCustom) copy.Layer = "S-WALL-STRUCTURAL-PLAN"; return copy; }).ToList();
        var material = new MaterialLayerRule { RuleId = "material-concrete", MaterialUniqueId = "material-uid-1", MaterialName = "콘크리트",
            ViewScope = ViewLayerScope.ArchitecturePlan, Layer = "A-MATL-CONC", Color = 8 };
        var scopedConfig = new RevitExportConfiguration { SchemaVersion = 3, OutputSetups = new() { new() { SetupName = "평면도별 설정",
            Layers = architectureRows.Concat(structuralRows).ToList(), MaterialRules = new() { material } } },
            SheetSets = new() { new() { Name = "기존 세트", TemplateId = "legacy", SheetUniqueIds = new() { "sheet-a" } } } };
        string scopedPath = Path.Combine(output, "scoped-config.json"); File.WriteAllText(scopedPath, System.Text.Json.JsonSerializer.Serialize(scopedConfig));
        var migrated = new ExportConfigurationStore(scopedPath).Load();
        check(migrated.SchemaVersion == 5 && migrated.OutputSetups.Count == 2
            && migrated.OutputSetups.All(s => s.Layers.All(r => r.ViewScope.Length == 0) && s.MaterialRules.All(r => r.ViewScope.Length == 0))
            && migrated.OutputSetups.Any(s => s.SetupName.EndsWith("구조평면도") && s.Layers.Any(r => r.Layer == "S-WALL-STRUCTURAL-PLAN")),
            "V18 plan scopes migrate to separate freely named DWG layer templates");
        check(migrated.SheetSets.Single().TemplateId.Length == 0 && migrated.SheetTemplateIds.Count == 0,
            "Legacy sheet membership is preserved but no template assignment is guessed");
        var portableMaterial = new OutputSetupFile { Name = "재료 템플릿", Layers = loaded.Layers,
            MaterialRules = new() { new() { MaterialUniqueId = "", MaterialName = "다른 프로젝트 재료", Layer = "A-MATL", Color = 42 } } };
        string materialPath = Path.Combine(output, "material-template.json"); portableMaterial.Save(materialPath); var importedMaterial = OutputSetupFile.Load(materialPath).MaterialRules.Single();
        check(importedMaterial.MaterialUniqueId.Length == 0 && importedMaterial.MaterialName == "다른 프로젝트 재료"
            && importedMaterial.Layer == "A-MATL" && importedMaterial.Color == 42,
            "Portable material templates retain material name, layer and color when the current project has no exact material");
        var rebound = RevitLayerMappingService.RebindImportedMaterials(new[] { importedMaterial,
            new MaterialLayerRule { MaterialName = "콘크리트", MaterialUniqueId = "old-project-id", Layer = "A-CONC", Color = 8 } },
            new[] { new MaterialChoice("current-project-id", 12, "콘크리트") });
        check(rebound[0].MaterialUniqueId.Length == 0 && rebound[0].Layer == "A-MATL" && rebound[0].Color == 42
            && rebound[1].MaterialUniqueId == "current-project-id",
            "Material import binds only an exact current-project name while retaining unresolved layer and color data");
        var duplicateMaterial = material.Copy(); duplicateMaterial.RuleId = "other";
        check(RevitLayerMappingService.ValidateMaterialRules(new[] { material, duplicateMaterial }).Count > 0,
            "The same exact Revit material cannot be assigned twice in one plan scope");
        duplicateMaterial.ViewScope = ViewLayerScope.CeilingPlan;
        check(RevitLayerMappingService.ValidateMaterialRules(new[] { material, duplicateMaterial }).Count > 0,
            "The same exact Revit material cannot be duplicated inside one DWG layer template");
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

        var invisible = RevitCategoryCatalog.DefaultRow("선", "<보이지 않는 선>", "Annotation");
        check(RevitLayerMappingService.IsInvisibleLineRow(invisible)
            && RevitLayerMappingService.InternalExcludedLayers(new[] { invisible }).Single() == RevitLayerMappingService.InvisibleLineExportLayer,
            "Revit invisible-line subcategory is recognized independently of its user layer");
    }
}
