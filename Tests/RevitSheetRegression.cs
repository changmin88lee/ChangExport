using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using ChangExport.DwgProcessing;
using CSMath;

internal static class RevitSheetRegression
{
    public static void Run(string output, Action<bool, string> check, Action<double, double, string> near)
    {
        var leaf = new CadDocument(ACadVersion.AC1024);
        leaf.Header.InsUnits = UnitsType.Millimeters;
        leaf.Layers.Add(new Layer("S-COL") { Color = new ACadSharp.Color(3) });
        leaf.Entities.Add(new Line { Layer = leaf.Layers["S-COL"], StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 1000, 0) });
        leaf.Entities.Add(new Line { StartPoint = new XYZ(0, 100, 0), EndPoint = new XYZ(1000, 100, 0), Color = new ACadSharp.Color(201) });
        leaf.Entities.Add(new MText { Value = "실제 뷰 본체", Height = 100, InsertPoint = new XYZ(500, 500, 0) });
        string leafPath = Path.Combine(output, "revit-view.dwg"); DwgWriter.Write(leafPath, leaf);
        var doc = new CadDocument(ACadVersion.AC1024); doc.Header.InsUnits = UnitsType.Millimeters;
        doc.PaperSpace.Layout.PaperUnits = PlotPaperUnits.Inches; // Revit's misleading plotter metadata.
        // The SDK protects removal of its first viewport. Reuse that slot as an
        // actual drawing viewport, as observed in native Revit sheet DWGs.
        var first = doc.PaperSpace.Entities.OfType<Viewport>().First();
        first.Center = new XYZ(100, 100, 0); first.Width = 100; first.Height = 100; first.ViewHeight = 10000;
        first.ViewCenter = new XY(500, 500); first.ViewDirection = XYZ.AxisZ; first.ActiveStatus = 1; first.Status = ViewportStatusFlags.CurrentlyAlwaysEnabled;
        doc.PaperSpace.Entities.Add(new Viewport { Center = new XYZ(6, 4.5, 0), Width = 12, Height = 9, ViewHeight = 12,
            ViewCenter = new XY(6, 4.5), ViewDirection = XYZ.AxisZ, ActiveStatus = 1,
            Status = ViewportStatusFlags.CurrentlyAlwaysEnabled | ViewportStatusFlags.UcsIconVisibility });
        doc.PaperSpace.Entities.Add(new LwPolyline(new[] { XY.Zero, new XY(420, 0), new XY(420, 297), new XY(0, 297) }.Select(p => new LwPolyline.Vertex(p))) { IsClosed = true });
        doc.Entities.Add(new Insert(new BlockRecord("X1", Path.GetFileName(leafPath))) { Color = new ACadSharp.Color(200) });
        string input = Path.Combine(output, "revit-sheet-source.dwg"), target = Path.Combine(output, "revit-sheet-final.dwg"); DwgWriter.Write(input, doc);
        var result = new ManagedDwgProcessor().Run(new BridgeRequest { Operation = "Flatten", RevitSheet = true, OutputPath = target,
            ColorRemaps = new()
            {
                new() { MarkerAci = 200, Layer = "A-MATERIAL", Color = 4, RuleId = "material", RemapFills = true, BoundaryPriority = 700 },
                new() { MarkerAci = 201, Layer = "A-MATERIAL-CUT", Color = 4, RuleId = "material", RemapFills = true, BoundaryPriority = 700 }
            } }, input, output);
        var saved = DwgReader.Read(target);
        check(!saved.Entities.OfType<Insert>().Any(), "Simple Revit output is individual entities, not a sheet block");
        near(result.ModelScale, 100, "First actual viewport determines 1:100 model scaling, not default paper viewport");
        near(saved.Header.ModelSpaceExtMax.X - saved.Header.ModelSpaceExtMin.X, 42000, "A3 frame enlarged 100 times in millimeters");
        near(saved.Header.ModelSpaceExtMax.Y - saved.Header.ModelSpaceExtMin.Y, 29700, "A3 frame height enlarged 100 times");
        var sourceLine = saved.Entities.OfType<Line>().Single(line => line.Layer.Name == "S-COL");
        near(sourceLine.EndPoint.DistanceFrom(sourceLine.StartPoint), Math.Sqrt(2) * 1000, "Referenced line actual length retained");
        check(result.FilterContainerMarkersIgnored == 1
            && saved.Entities.OfType<Line>().Count(line => line.Layer.Name == "S-COL") == 1
            && saved.Entities.OfType<Line>().Count(line => line.Layer.Name == "A-MATERIAL-CUT") == 1
            && !saved.Entities.Any(entity => entity.Layer.Name == "A-MATERIAL"),
            "Material marker on a placed-view XREF does not recolor unrelated view geometry; leaf material markers still remap");
        check(result.ConvertedViewports == 1, "Default layout viewport skipped independently of order");
        check(saved.BlockRecords.All(b => (b.Flags & (BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay)) == 0), "No external reference remains");
        check(saved.BlockRecords.SelectMany(b => b.Entities).Any(e => e is Line && e.Layer.Name == "S-COL"), "Referenced model geometry and Revit category layer name retained");
        check(saved.BlockRecords.SelectMany(b => b.Entities).OfType<MText>().Any(t => t.Value == "실제 뷰 본체"), "Referenced view text not lost");
        check(DwgReader.Read(input).BlockRecords.Any(b => b.Flags.HasFlag(BlockTypeFlags.XRef)), "Native staging source is not rewritten");
    }
}
