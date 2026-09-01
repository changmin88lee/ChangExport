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
    }
}
