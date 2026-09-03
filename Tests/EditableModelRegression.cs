using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using CSMath;
using Color = ACadSharp.Color;

internal static class EditableModelRegression
{
    public static void Run(string output, Action<bool, string> check, Action<double, double, string> near)
    {
        var processor = new ManagedDwgProcessor();
        string Convert(CadDocument doc, string name, out BridgeResponse result)
        {
            string input = Path.Combine(output, name + "-native.dwg"), target = Path.Combine(output, name + ".dwg");
            DwgWriter.Write(input, doc);
            result = processor.Run(new BridgeRequest { Operation = "Flatten", RevitSheet = true, OutputPath = target }, input, output);
            return target;
        }
        string first = Convert(Sheet(100), "scale100", out var response100);
        string second = Convert(Sheet(200), "scale200", out var response200);
        foreach (var (file, scale) in new[] { (first, 100), (second, 200) })
        {
            var doc = DwgReader.Read(file);
            Entity[] all = DwgRegression.Walk(doc.ModelSpace).ToArray();
            check(doc.Entities.OfType<Insert>().All(insert => insert.SpatialFilter != null),
                "Only exact per-entity viewport clipping wrappers remain in a simple scaled sheet");
            near(doc.Header.ModelSpaceExtMax.X, 420 * scale, "Sheet width follows per-sheet scale");
            near(doc.Header.ModelSpaceExtMax.Y, 297 * scale, "Sheet height follows per-sheet scale");
            var line = all.OfType<Line>().Single(l => l.Layer.Name == "WALL");
            near(line.StartPoint.DistanceFrom(line.EndPoint), 6000, "Both scales produce the same 6000 mm wall");
            near(line.StartPoint.X, 100 * scale - 3000, "Viewport center translation composed with sheet scale");
            near(line.StartPoint.Y, 100 * scale, "Viewport Y position");
            var crossing = all.OfType<Line>().Single(l => l.Layer.Name == "CROSSING");
            near(crossing.StartPoint.DistanceFrom(crossing.EndPoint), 100 * scale, "Long line clipped to visible viewport, no hidden extension");
            var paper = all.OfType<Line>().Single(l => l.Layer.Name == "FRAME_DETAIL");
            near(paper.StartPoint.X, 20 * scale, "Nonzero block origin respected before rotation and scale");
            near(paper.StartPoint.Y, 25 * scale, "Block origin Y");
            near(paper.EndPoint.X, 20 * scale, "Rotated endpoint X");
            near(paper.EndPoint.Y, 45 * scale, "Rotated endpoint Y");
            check(paper.Color.Index == 5, "ByBlock color resolved when block is removed");
            var constant = all.OfType<TextEntity>().Single(t => t.Value == "고정 도곽 속성");
            near(constant.Height, 2 * scale, "Constant attribute is preserved as editable text");
            check(!all.OfType<TextEntity>().Any(t => t.Value == "숨김 속성"), "Hidden attribute remains hidden");
            near(all.OfType<MText>().Single().Height, 3 * scale, "Paper text height enlarged with frame");
            var hatch = all.OfType<Hatch>().Single();
            var arc = (Hatch.BoundaryPath.Arc)hatch.Paths.Single().Edges.Single();
            near(arc.Radius, 100, "Hatch radius does not absorb translation");
            near(arc.Center.X, 100 * scale - 2000, "Hatch center transformed correctly");
            near(Math.Abs(arc.EndAngle - arc.StartAngle), 2 * Math.PI, "Full circular hatch stays closed");
            var dim = all.OfType<DimensionAligned>().Single();
            near(dim.Measurement, 6000, "Dimension remains native at full size");
            near(dim.Style.LinearScaleFactor, 1, "Full-size dimension value is not multiplied by sheet scale");
            check(dim.Block.Entities.Count > 0, "Native dimension display definition retained");
        }
        near(response100.ModelScale, 100, "1:100 scale reported"); near(response200.ModelScale, 200, "1:200 scale reported");
        foreach (string direction in new[] { "Horizontal", "Vertical" })
        foreach (double gap in new[] { 0d, 5000d })
        {
            string path = Path.Combine(output, $"mixed-set-{direction}-{gap}.dwg");
            var memoryInputs = new[] { "scale100-native.dwg", "scale200-native.dwg" }.Select(name => processor.Prepare(
                new BridgeRequest { RevitSheet = true }, Path.Combine(output, name))).ToList();
            var result = processor.MergePrepared(new BridgeRequest { Operation = "Merge", RevitSheet = true,
                OutputPath = path, Direction = direction, MarginMm = gap }, memoryInputs, output);
            var doc = DwgReader.Read(path);
            check(doc.Entities.OfType<Insert>().All(insert => insert.SpatialFilter != null),
                "Merged set has no sheet container blocks; only exact clipping wrappers remain");
            near(result.Placements[0].Width, 42000, "First sheet keeps 100-scale frame after merge");
            near(result.Placements[1].Width, 84000, "Second sheet keeps 200-scale frame after merge");
            if (direction == "Horizontal") near(result.Placements[1].X, 42000 + gap, "Horizontal gap measured after scaling");
            else near(result.Placements[0].Y - (result.Placements[1].Y + result.Placements[1].Height), gap, "Vertical gap measured after scaling");
            check(DwgRegression.Walk(doc.ModelSpace).OfType<Line>().Count(l => l.Layer.Name == "WALL" && Math.Abs(l.StartPoint.DistanceFrom(l.EndPoint) - 6000) < 1e-4) == 2,
                "Merge preserves both real-size walls");
        }
        var mixed = Sheet(100);
        mixed.PaperSpace.Entities.Add(new Viewport { Center = new XYZ(250, 100, 0), Width = 40, Height = 40, ViewHeight = 2000,
            ViewCenter = new XY(3000, 0), ViewDirection = XYZ.AxisZ, ActiveStatus = 1 });
        string mixedPath = Convert(mixed, "mixed-views", out var mixedResult);
        near(mixedResult.ModelScale, 100, "Largest viewport determines mixed sheet scale");
        check(mixedResult.Warnings.Any(w => w.Contains("혼합 축척")), "Mixed-scale policy reported explicitly");
        check(DwgRegression.Walk(DwgReader.Read(mixedPath).ModelSpace).OfType<Line>().Any(l => l.Layer.Name == "WALL" && Math.Abs(l.StartPoint.DistanceFrom(l.EndPoint) - 4000) < 1e-4),
            "Smaller 1:50 viewport preserves relative magnification and clips to its 40 mm frame");
        var concave = Sheet(100);
        var concaveViewport = concave.PaperSpace.Entities.OfType<Viewport>().Last();
        var concaveBoundary = new LwPolyline(new[]
        {
            new XY(50, 50), new XY(150, 50), new XY(150, 150), new XY(100, 100), new XY(50, 150)
        }.Select(point => new LwPolyline.Vertex(point))) { IsClosed = true };
        concave.PaperSpace.Entities.Add(concaveBoundary); concaveViewport.Boundary = concaveBoundary;
        concaveViewport.Status |= ViewportStatusFlags.NonRectangularClipping;
        string concavePath = Convert(concave, "concave-clip", out var concaveResult);
        var concaveWrappers = DwgReader.Read(concavePath).Entities.OfType<Insert>()
            .Where(insert => insert.SpatialFilter != null).ToArray();
        check(concaveResult.BoundaryBlocksRetained > 0 && concaveWrappers.Length > 0
            && concaveWrappers.All(insert => insert.SpatialFilter.BoundaryPoints.Count == 5),
            "Every nonlinear entity under a concave viewport keeps the exact five-point clipping boundary");
        var mirrored = Sheet(100);
        var mirrorBlock = new BlockRecord("MIRRORED_LOGO");
        mirrorBlock.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(10, 0, 0) });
        var fill = new Hatch { IsSolid = true };
        fill.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[] { new Hatch.BoundaryPath.Arc
        { Center = new XY(2, 2), Radius = 1, StartAngle = 0, EndAngle = 2 * Math.PI, CounterClockWise = true } }));
        mirrorBlock.Entities.Add(fill);
        mirrored.PaperSpace.Entities.Add(new Insert(mirrorBlock) { Normal = -XYZ.AxisZ, InsertPoint = new XYZ(-20, 25, 0) });
        string mirrorPath = Convert(mirrored, "mirrored-solid", out _);
        var mirrorDoc = DwgReader.Read(mirrorPath);
        check(mirrorDoc.Entities.OfType<Insert>().All(insert => insert.SpatialFilter != null),
            "Mirrored geometry is flattened, with only exact clipping wrappers retained");
        var mirrorHatch = DwgRegression.Walk(mirrorDoc.ModelSpace).OfType<Hatch>().Single(h => Math.Abs(((Hatch.BoundaryPath.Arc)h.Paths[0].Edges[0]).Center.X - 1800) < 1e-4);
        near(((Hatch.BoundaryPath.Arc)mirrorHatch.Paths[0].Edges[0]).Center.Y, 2700, "Mirrored solid hatch center in world coordinates");
        near(mirrorHatch.Normal.Z, 1, "No second mirror caused by hatch OCS normal");
    }

    private static CadDocument Sheet(int scale)
    {
        var doc = new CadDocument(ACadVersion.AC1024);
        doc.PaperSpace.Layout.PaperUnits = PlotPaperUnits.Millimeters;
        foreach (string name in new[] { "WALL", "CROSSING", "FRAME_DETAIL" }) doc.Layers.Add(new Layer(name));
        doc.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(6000, 0, 0), Layer = doc.Layers["WALL"] });
        doc.Entities.Add(new Line { StartPoint = new XYZ(-50000, 500, 0), EndPoint = new XYZ(50000, 500, 0), Layer = doc.Layers["CROSSING"] });
        var hatch = new Hatch { IsSolid = true };
        hatch.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[] { new Hatch.BoundaryPath.Arc
        { Center = new XY(1000, 1000), Radius = 100, StartAngle = 0, EndAngle = 2 * Math.PI, CounterClockWise = true } })); doc.Entities.Add(hatch);
        var dimension = new DimensionAligned(XYZ.Zero, new XYZ(6000, 0, 0)) { Offset = 100 };
        doc.Entities.Add(dimension); dimension.UpdateBlock();
        var paper = doc.PaperSpace.Entities.OfType<Viewport>().First();
        paper.Center = new XYZ(6, 4.5, 0); paper.Width = 12; paper.Height = 9; paper.ViewHeight = 12;
        paper.ViewCenter = new XY(6, 4.5); paper.Status = ViewportStatusFlags.CurrentlyAlwaysEnabled | ViewportStatusFlags.UcsIconVisibility;
        paper.ActiveStatus = 1;
        doc.PaperSpace.Entities.Add(new Viewport { Center = new XYZ(100, 100, 0), Width = 100, Height = 100, ViewHeight = 100 * scale,
            ViewCenter = new XY(3000, 0), ViewDirection = XYZ.AxisZ, ActiveStatus = 1 });
        doc.PaperSpace.Entities.Add(new LwPolyline(new[] { XY.Zero, new XY(420, 0), new XY(420, 297), new XY(0, 297) }.Select(p => new LwPolyline.Vertex(p))) { IsClosed = true });
        doc.PaperSpace.Entities.Add(new MText { Value = "도곽 문자", InsertPoint = new XYZ(10, 280, 0), Height = 3, RectangleWidth = 40 });
        var block = new BlockRecord("NESTED"); block.BlockEntity.BasePoint = new XYZ(5, 7, 0);
        block.Entities.Add(new Line { StartPoint = new XYZ(5, 7, 0), EndPoint = new XYZ(15, 7, 0), Color = Color.ByBlock });
        block.Entities.Add(new AttributeDefinition { Tag = "CONST", Value = "고정 도곽 속성", InsertPoint = new XYZ(5, 7, 0), Height = 1, Flags = AttributeFlags.Constant });
        block.Entities.Add(new AttributeDefinition { Tag = "HIDDEN", Value = "숨김 속성", InsertPoint = new XYZ(5, 7, 0), Height = 1, Flags = AttributeFlags.Constant | AttributeFlags.Hidden });
        doc.PaperSpace.Entities.Add(new Insert(block) { InsertPoint = new XYZ(20, 25, 0), XScale = 2, YScale = 2, ZScale = 2,
            Rotation = Math.PI / 2, Layer = doc.Layers["FRAME_DETAIL"], Color = new Color(5) });
        return doc;
    }
}
