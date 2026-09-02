using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using ChangExport.Models;
using CSMath;

internal static class CustomLayerRegression
{
    public static void Run(string output, Action<bool, string> check)
    {
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
        var hostBoundary = new Line { StartPoint = finishBoundary.StartPoint, EndPoint = finishBoundary.EndPoint,
            Layer = native, Color = new ACadSharp.Color(204) };
        var wrappedEnd = new Line { StartPoint = new XYZ(100, 50, 0), EndPoint = new XYZ(115, 50, 0),
            Layer = native, Color = new ACadSharp.Color(204) };
        materials.Entities.Add(finishBoundary); materials.Entities.Add(structureBoundary);
        materials.Entities.Add(hostBoundary); materials.Entities.Add(wrappedEnd);
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
                new() { MarkerAci = 202, Layer = "A-FINISH", Color = 30, RuleId = "finish", RemapFills = true, BoundaryPriority = 700 },
                new() { MarkerAci = 203, Layer = "A-STRUCTURE", Color = 8, RuleId = "structure", RemapFills = true, BoundaryPriority = 500 },
                new() { MarkerAci = 204, Layer = "A-FINISH", Color = 30, RuleId = "wrap:finish", BoundaryPriority = 1 }
            } }, materialInput, output);
        var materialSaved = DwgReader.Read(materialOutput);
        var materialEntities = materialSaved.BlockRecords.SelectMany(record => record.Entities).ToArray();
        var savedFill = materialEntities.OfType<Hatch>().Single(h => h.Layer.Name == "A-FINISH");
        check(!savedFill.Color.IsByLayer && savedFill.Color.R == 12 && savedFill.Color.G == 180 && savedFill.Color.B == 44
            && savedFill.Pattern?.Name == "CE_TEST_PATTERN"
            && Math.Abs(savedFill.PatternScale - 3.25) < 1e-8 && Math.Abs(savedFill.PatternAngle - .42) < 1e-8,
            "Material filter moves hatch layer while retaining Revit color, pattern scale and angle");
        check(materialEntities.OfType<Line>().Count(line => line.StartPoint.X == 100 && line.EndPoint.X == 100
                && line.Layer.Name == "A-FINISH") == 1
            && !materialEntities.OfType<Line>().Any(line => line.StartPoint.X == 100 && line.EndPoint.X == 100
                && line.Layer.Name == "A-STRUCTURE")
            && materialResponse.MaterialBoundaryDuplicatesRemoved == 2,
            "Shared compound boundary keeps the material Part ahead of Structure and host support");
        check(materialEntities.OfType<Line>().Count(line => line.StartPoint.Y == 50 && line.EndPoint.Y == 50
                && line.Layer.Name == "A-FINISH") == 1,
            "Host-only wrapped end boundary remains on the wrapping material layer");
        check(materialEntities.OfType<Line>().Any(line => line.StartPoint.X == 120 && line.Layer.Name == "NATIVE-MATERIAL")
            && materialResponse.FilterLowerGraphicsSkipped == 1,
            "Beyond lower graphic is excluded from type/material remapping");
    }
}
