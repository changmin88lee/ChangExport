using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using ChangExport.Export;
using ChangExport.Models;
using CSMath;

internal static class CustomLayerRegression
{
    public static void Run(string output, Action<bool, string> check)
    {
        check(TemporaryFilterExport.CompoundLayerBoundaryPriority(0)
                < TemporaryFilterExport.CompoundLayerBoundaryPriority(3),
            "Compound wall/floor shared boundaries prefer the interior/bottom layer index");
        var first = new RevitLayerRow { IsCustom = true, RuleId = "RC", Category = "구조 기둥", TypeNameContains = "RC", Layer = "S-RC", CutLayer = "S-RC-CUT", Color = 3, CutColor = 1 };
        var second = first.Copy(); second.RuleId = "400"; second.TypeNameContains = "400";
        check(first.Matches("구조 기둥", "rc-400") && !first.Matches("벽", "RC-400") && !first.Matches("구조 기둥", "철골"), "Contains is case-insensitive and category-scoped");
        check(new[] { first, second }.First(r => r.Matches("구조 기둥", "RC-400")).RuleId == "RC", "First matching rule wins");
        first.TypeNameContains = " "; check(!first.Matches("구조 기둥", "RC 400"), "Blank filters cannot match all elements");
        var source = DwgRegression.Sheet(ACadVersion.AC1024);
        var markerLayer = new Layer("Revit-generated-override") { Color = new ACadSharp.Color(200) }; source.Layers.Add(markerLayer);
        source.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(10, 10, 0), Layer = markerLayer });
        var block = new BlockRecord("TYPE_RC_400");
        block.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(10, 0, 0) });
        block.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(0, 10, 0), Color = new ACadSharp.Color(201) });
        var viewFill = new Hatch { IsSolid = true, Color = new ACadSharp.Color(12, 180, 44) };
        viewFill.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Polyline(new[] { new XYZ(1, 1, 0), new XYZ(5, 1, 0), new XYZ(5, 5, 0), new XYZ(1, 5, 0) })
        }));
        block.Entities.Add(viewFill);
        var rgb = new ACadSharp.Color(200);
        source.Entities.Add(new Insert(block) { Color = new ACadSharp.Color(rgb.R, rgb.G, rgb.B) });
        source.PaperSpace.Entities.Add(new MText { Value = "시트 CE_TMP_TEST", Height = 2, InsertPoint = new XYZ(10, 10, 0) });
        string input = Path.Combine(output, "filter-source.dwg"), target = Path.Combine(output, "filter-final.dwg");
        DwgWriter.Write(input, source);
        var request = new BridgeRequest { Operation = "Flatten", OutputPath = target,
            TextReplacements = new() { ["CE_TMP_TEST"] = "AA-101" }, ColorRemaps = new()
            {
                new() { MarkerAci = 200, Layer = "S-RC", Color = 3, RuleId = "RC" },
                new() { MarkerAci = 201, Layer = "S-RC-CUT", Color = 1, RuleId = "RC" }
            } };
        new ManagedDwgProcessor().Run(request, input, output);
        var final = DwgReader.Read(target);
        var all = final.BlockRecords.SelectMany(b => b.Entities).ToList();
        check(all.Count(e => e is Line && e.Layer.Name == "S-RC") == 2, "Layer-color marker and nested true-color parent both remap");
        check(all.Count(e => e is Line && e.Layer.Name == "S-RC-CUT") == 1, "Cut marker overrides inherited projection marker");
        check(all.OfType<Hatch>().Any(h => !h.Color.IsByLayer && h.Color.R == 12 && h.Color.G == 180 && h.Color.B == 44),
            "Custom layer marker does not replace the Revit view color of a nested fill");
        check(all.Any(e => e is Line && e.Layer.Name == "S-COL"), "Unmatched category geometry retained");
        check(final.Layers["S-RC"].Color.Index == 3 && final.Layers["S-RC-CUT"].Color.Index == 1, "Target ACI colors persist in reopened DWG");
        check(all.OfType<MText>().Any(t => t.Value == "시트 AA-101"), "Temporary sheet identifier restored in DWG text");
        check(!ManagedDwgProcessor.UsedColorIndices(new[] { target }).Contains(201), "No active cut marker color remains in entities");

        // Two parent types sharing one nested family must not recolor each other.
        var shared = new BlockRecord("SHARED_NESTED"); shared.Entities.Add(new Line { EndPoint = new XYZ(10, 0, 0) });
        var a = new BlockRecord("TYPE_A"); a.Entities.Add(new Insert(shared));
        var b = new BlockRecord("TYPE_B"); b.Entities.Add(new Insert(shared));
        var paired = DwgRegression.Sheet(ACadVersion.AC1024);
        paired.Entities.Add(new Insert(a) { Color = new ACadSharp.Color(200) });
        paired.Entities.Add(new Insert(b) { Color = new ACadSharp.Color(201), InsertPoint = new XYZ(20, 0, 0) });
        string pairInput = Path.Combine(output, "shared-filter-source.dwg"), pairOutput = Path.Combine(output, "shared-filter-final.dwg");
        DwgWriter.Write(pairInput, paired); request.OutputPath = pairOutput;
        new ManagedDwgProcessor().Run(request, pairInput, output);
        var pairSaved = DwgReader.Read(pairOutput);
        var nested = pairSaved.BlockRecords.Where(block => block.Name.EndsWith("SHARED_NESTED")).ToList();
        check(nested.Count == 2 && nested.Select(block => block.Entities.Single().Layer.Name).ToHashSet().SetEquals(new[] { "S-RC", "S-RC-CUT" }),
            "Shared nested block definitions remain independent under different parent filters");

        // Exact compound-material remaps move fills without changing their Revit
        // appearance, protect lower graphics, and deduplicate shared boundaries.
        var materials = DwgRegression.Sheet(ACadVersion.AC1024);
        var native = new Layer("NATIVE-MATERIAL") { Color = new ACadSharp.Color(7) }; materials.Layers.Add(native);
        var beyond = new LineType("Beyond"); materials.LineTypes.Add(beyond);
        var finishBoundary = new Line { StartPoint = new XYZ(100, 0, 0), EndPoint = new XYZ(100, 50, 0),
            Layer = native, Color = new ACadSharp.Color(202) };
        var structureBoundary = new Line { StartPoint = finishBoundary.StartPoint, EndPoint = finishBoundary.EndPoint,
            Layer = native, Color = new ACadSharp.Color(203) };
        var floorBoundary = new Line { StartPoint = finishBoundary.StartPoint, EndPoint = finishBoundary.EndPoint,
            Layer = native, Color = new ACadSharp.Color(206) };
        var wrappedEnd = new Line { StartPoint = new XYZ(100, 50, 0), EndPoint = new XYZ(115, 50, 0),
            Layer = native, Color = new ACadSharp.Color(204) };
        var airOverWrappedEnd = new Line { StartPoint = new XYZ(105, 50, 0), EndPoint = new XYZ(110, 50, 0),
            Layer = native, Color = new ACadSharp.Color(205) };
        materials.Entities.Add(finishBoundary); materials.Entities.Add(structureBoundary); materials.Entities.Add(floorBoundary);
        materials.Entities.Add(wrappedEnd); materials.Entities.Add(airOverWrappedEnd);
        materials.Entities.Add(new Line { StartPoint = new XYZ(120, 0, 0), EndPoint = new XYZ(120, 50, 0),
            Layer = native, LineType = beyond, Color = new ACadSharp.Color(202) });
        var materialBlock = new BlockRecord("MATERIAL_PART");
        materialBlock.Entities.Add(new Line { EndPoint = new XYZ(20, 0, 0) });
        var testPattern = new HatchPattern("CE_TEST_PATTERN");
        testPattern.Lines.Add(new HatchPattern.Line { Angle = 0, BasePoint = XY.Zero, Offset = new XY(0, 2), DashLengths = new() { 4, -1 } });
        var materialFill = new Hatch { IsSolid = false, Pattern = testPattern, PatternType = HatchPatternType.Custom,
            PatternScale = 3.25, PatternAngle = .42,
            Color = new ACadSharp.Color(12, 180, 44) };
        materialFill.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Polyline(new[] { new XYZ(1, 1, 0), new XYZ(5, 1, 0), new XYZ(5, 5, 0), new XYZ(1, 5, 0) })
        }));
        materialBlock.Entities.Add(materialFill);
        materials.Entities.Add(new Insert(materialBlock) { Color = new ACadSharp.Color(202) });
        string materialInput = Path.Combine(output, "material-filter-source.dwg");
        string materialOutput = Path.Combine(output, "material-filter-final.dwg");
        DwgWriter.Write(materialInput, materials);
        var materialResponse = new ManagedDwgProcessor().Run(new BridgeRequest { Operation = "Flatten", OutputPath = materialOutput,
            ColorRemaps = new()
            {
                new() { MarkerAci = 202, Layer = "A-FINISH", Color = 30, RuleId = "finish", RemapFills = true,
                    BoundaryPriority = 1, SourceLayers = new() { "벽" } },
                new() { MarkerAci = 203, Layer = "A-STRUCTURE", Color = 8, RuleId = "structure", RemapFills = true,
                    BoundaryPriority = 2, SourceLayers = new() { "벽" } },
                new() { MarkerAci = 204, Layer = "A-FINISH", Color = 30, RuleId = "wrap:finish",
                    BoundaryPriority = 1, SourceLayers = new() { "벽" } },
                new() { MarkerAci = 205, Layer = "A-AIR", Color = 254, RuleId = "air", RemapFills = true,
                    BoundaryPriority = 100, SourceLayers = new() { "벽" } },
                new() { MarkerAci = 206, Layer = "A-FLOOR", Color = 4, RuleId = "floor", RemapFills = true,
                    BoundaryPriority = 1, SourceLayers = new() { "바닥" } }
            } }, materialInput, output);
        var materialSaved = DwgReader.Read(materialOutput);
        var materialEntities = materialSaved.BlockRecords.SelectMany(record => record.Entities).ToArray();
        var savedFill = materialEntities.OfType<Hatch>().Single(h => h.Layer.Name == "A-FINISH");
        check(!savedFill.Color.IsByLayer && savedFill.Color.R == 12 && savedFill.Color.G == 180 && savedFill.Color.B == 44
            && savedFill.Pattern?.Name == "CE_TEST_PATTERN"
            && Math.Abs(savedFill.PatternScale - 3.25) < 1e-8 && Math.Abs(savedFill.PatternAngle - .42) < 1e-8,
            "Material filter moves hatch layer while retaining Revit color, pattern scale and angle");
        check(materialEntities.OfType<Line>().Count(line => line.StartPoint.X == 100 && line.EndPoint.X == 100
                && line.Layer.Name == "A-STRUCTURE") == 1
            && !materialEntities.OfType<Line>().Any(line => line.StartPoint.X == 100 && line.EndPoint.X == 100
                && line.Layer.Name == "A-FINISH")
            && materialResponse.MaterialBoundaryDuplicatesRemoved == 2,
            "Shared compound boundary is owned once by the interior-side layer");
        check(materialEntities.OfType<Line>().Count(line => line.StartPoint.X == 100 && line.EndPoint.X == 100
                && line.Layer.Name == "A-FLOOR") == 1,
            "Coincident material boundaries from disjoint wall and floor source scopes remain independent");
        check(materialEntities.OfType<Line>().Count(line => line.StartPoint.Y == 50 && line.EndPoint.Y == 50
                && line.Layer.Name == "A-FINISH") == 1
            && !materialEntities.OfType<Line>().Any(line => line.StartPoint.Y == 50 && line.EndPoint.Y == 50
                && line.Layer.Name == "A-AIR"),
            "Host wrapped end outranks a partially overlapping air Part boundary");
        check(materialEntities.OfType<Line>().Any(line => line.StartPoint.X == 120 && line.Layer.Name == "NATIVE-MATERIAL")
            && materialResponse.FilterLowerGraphicsSkipped == 1,
            "Beyond lower graphic is excluded from type/material remapping");

        var rewrittenLayer = DwgRegression.Sheet(ACadVersion.AC1024);
        var nativeSky = new Layer("선__04_하늘_") { Color = new ACadSharp.Color(0, 166, 0) };
        rewrittenLayer.Layers.Add(nativeSky);
        rewrittenLayer.Entities.Add(new Line { Layer = nativeSky, EndPoint = new XYZ(10, 0, 0) });
        string rewrittenInput = Path.Combine(output, "revit-layer-name-source.dwg");
        string rewrittenOutput = Path.Combine(output, "revit-layer-name-final.dwg");
        DwgWriter.Write(rewrittenInput, rewrittenLayer);
        new ManagedDwgProcessor().Run(new BridgeRequest { Operation = "Flatten", UseLayerColors = true,
            OutputPath = rewrittenOutput,
            LayerStyles = new() { new() { Layer = "선_#04(하늘)", Color = 7 } } }, rewrittenInput, output);
        var rewrittenSaved = DwgReader.Read(rewrittenOutput);
        check(rewrittenSaved.Layers["선__04_하늘_"].Color.Index == 7
            && DwgRegression.Walk(rewrittenSaved.ModelSpace).Single(entity => entity.Layer.Name == "선__04_하늘_").Color.IsByLayer,
            "Revit-rewritten # and parenthesis layer names receive the configured layer color");

        // The detached DWG remapper still preserves fill appearance when a caller
        // supplies an explicit appearance mapping. Revit link exports now use
        // link-derived Parts and unique color markers instead of this fallback.
        var linked = DwgRegression.Sheet(ACadVersion.AC1024);
        var linkedBlock = new BlockRecord("LINKED_APARTMENT_MODEL");
        var linkedPattern = new HatchPattern("LINK_BRICK_75");
        linkedPattern.Lines.Add(new HatchPattern.Line { Angle = 0, BasePoint = XY.Zero, Offset = new XY(0, 7.5) });
        var linkedFill = new Hatch { Pattern = linkedPattern, PatternType = HatchPatternType.Custom,
            PatternScale = 2, Color = new ACadSharp.Color(92, 61, 43) };
        linkedFill.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Polyline(new[] { new XYZ(0, 0, 0), new XYZ(20, 0, 0),
                new XYZ(20, 10, 0), new XYZ(0, 10, 0) })
        }));
        linkedBlock.Entities.Add(linkedFill);
        linkedBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(20, 0, 0) });
        linkedBlock.Entities.Add(new Line { StartPoint = new XYZ(20, 0, 0), EndPoint = new XYZ(20, 10, 0) });
        linkedBlock.Entities.Add(new Line { StartPoint = new XYZ(20, 10, 0), EndPoint = new XYZ(0, 10, 0) });
        linkedBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 10, 0), EndPoint = new XYZ(0, 0, 0) });
        linkedBlock.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(20, 10, 0) });
        linked.Entities.Add(new Insert(linkedBlock) { Color = new ACadSharp.Color(200) });
        string linkedInput = Path.Combine(output, "linked-material-source.dwg");
        string linkedOutput = Path.Combine(output, "linked-material-final.dwg");
        DwgWriter.Write(linkedInput, linked);
        var linkedResponse = new ManagedDwgProcessor().Run(new BridgeRequest { Operation = "Flatten", OutputPath = linkedOutput,
            ColorRemaps = new() { new() { MarkerAci = 200, Layer = "A-LINK-TYPE", Color = 3, RuleId = "link-type" } },
            MaterialAppearanceRemaps = new()
            {
                new() { Pattern = "REVIT_LINK_BRICK", DisplayRgb = (92 << 16) | (61 << 8) | 43,
                    Layer = "A-LINK-BRICK", Color = 30, RuleId = "link-brick", MaterialName = "벽돌",
                    BoundaryPriority = 700, PatternLines = new() { new() { SpacingMm = 15 } } }
            }
        }, linkedInput, output);
        var linkedEntities = DwgRegression.Walk(DwgReader.Read(linkedOutput).ModelSpace).ToArray();
        var linkedSavedFill = linkedEntities.OfType<Hatch>().Single(hatch => hatch.Pattern?.Name == "LINK_BRICK_75");
        check(linkedSavedFill.Layer.Name == "A-LINK-BRICK" && linkedSavedFill.Pattern?.Name == "LINK_BRICK_75"
            && linkedSavedFill.Color.R == 92 && linkedSavedFill.Color.G == 61 && linkedSavedFill.Color.B == 43,
            "Linked material appearance remap preserves the native Revit hatch");
        check(linkedEntities.OfType<Line>().Count(line => line.Layer.Name == "A-LINK-BRICK") == 4
            && linkedEntities.OfType<Line>().Count(line => line.Layer.Name == "A-LINK-TYPE") == 1
            && linkedResponse.LinkedMaterialFillsRemapped == 1 && linkedResponse.LinkedMaterialBoundariesRemapped == 4,
            "Linked material hatch owns its four exact boundaries while unrelated geometry keeps the linked type rule");
    }
}
