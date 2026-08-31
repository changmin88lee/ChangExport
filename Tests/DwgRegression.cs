using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using CSMath;
using CadColor = ACadSharp.Color;

internal static class DwgRegression
{
    public static void Run(string output, Action<bool, string> check, Action<double, double, string> near)
    {
        var processor = new ManagedDwgProcessor();
        string Save(CadDocument doc, string name)
        {
            string path = Path.Combine(output, name + ".dwg"); DwgWriter.Write(path, doc); return path;
        }
        string Flatten(CadDocument doc, string name)
        {
            string source = Save(doc, name + "-source"); string flat = Path.Combine(output, name + ".dwg");
            processor.Run(new BridgeRequest { Operation = "Flatten", OutputPath = flat }, source, output); return flat;
        }
        void Reject(CadDocument doc, string name, string expected)
        {
            bool rejected = false;
            try { Flatten(doc, name); }
            catch (Exception e) when (e.Message.Contains(expected)) { rejected = true; }
            check(rejected && !File.Exists(Path.Combine(output, name + ".dwg")), name + " rejects without a final file");
        }

        var native = Sheet();
        string first = Flatten(native, "rich");
        var reopened = DwgReader.Read(first);
        DxfWriter.Write(Path.Combine(output, "rich-crosscheck.dxf"), reopened);
        var sheet = ((Insert)reopened.Entities.Single()).Block;
        var view = sheet.Entities.OfType<Insert>().Single(i => i.SpatialFilter != null);
        check(sheet.Entities.OfType<MText>().Single().Value == "구조 평면도 · 도곽", "Korean paper text retained");
        check(view.Block.Entities.OfType<MText>().Single().Value == "기둥 C1 / 300×600", "Korean model text retained");
        check(view.Block.Entities.OfType<Hatch>().Single().Paths.Count == 1, "Hatch boundary retained");
        near(view.Block.Entities.OfType<DimensionAligned>().Single().Measurement, 100, "Dimension model measurement retained");
        check(view.Block.Entities.OfType<DimensionAligned>().Single().Block.Entities.Count > 0, "Dimension picture retained");
        check(!Walk(view.Block).Any(e => e.Layer.Name == "FROZEN"), "Viewport frozen layer removed inside nested blocks");
        check(reopened.Layers["S-COL"].Color.Index == 3, "ACI layer color retained");
        near(view.XScale, 0.1, "Viewport scale retained");
        near(Math.Sin(view.Rotation), -1, "Viewport twist sine");
        near(Math.Cos(view.Rotation), 0, "Viewport twist cosine");
        // Independently check a known WCS point using the stored CAD transform.
        XYZ point = view.GetTransform().ApplyTransform(new XYZ(30, 40, 0));
        near(point.X, 99, "Target X maps to sheet center minus DCS view center");
        near(point.Y, 98, "Target Y maps to sheet center minus DCS view center");
        XY[] expectedClip = { new(80, 90), new(120, 90), new(120, 110), new(80, 110) };
        for (int n = 0; n < expectedClip.Length; n++)
        {
            XY local = view.SpatialFilter.BoundaryPoints[n];
            XYZ p = view.GetTransform().ApplyTransform(new XYZ(local.X, local.Y, 0));
            near(p.X, expectedClip[n].X, "Clip corner sheet X"); near(p.Y, expectedClip[n].Y, "Clip corner sheet Y");
        }
        // A second sheet deliberately reuses block/style names with other content.
        var secondDoc = Sheet();
        secondDoc.Entities.OfType<MText>().Single().Value = "다른 시트";
        secondDoc.Entities.OfType<MText>().Single().Style.Filename = "arial.ttf";
        secondDoc.Entities.OfType<Insert>().Single().Block.Entities.OfType<Line>().First().EndPoint = new XYZ(700, 0, 0);
        string second = Flatten(secondDoc, "other");
        string mergedPath = Path.Combine(output, "different-sheets.dwg");
        processor.Run(new BridgeRequest { Operation = "Merge", Inputs = new() { first, second }, OutputPath = mergedPath, MarginMm = 25,
            LayerStyles = new() { new() { Layer = "S-COL", Linetype = "Continuous", Lineweight = 35 } } }, first, output);
        var merged = DwgReader.Read(mergedPath);
        DxfWriter.Write(Path.Combine(output, "merged-crosscheck.dxf"), merged);
        var textValues = merged.Entities.OfType<Insert>().Select(i => Walk(i.Block).OfType<MText>().Select(t => t.Value).ToList()).ToList();
        check(textValues[0].Contains("기둥 C1 / 300×600") && textValues[1].Contains("다른 시트"), "Same-name blocks do not replace another sheet");
        var texts = merged.Entities.OfType<Insert>().SelectMany(i => Walk(i.Block)).OfType<MText>().ToList();
        check(texts.Single(t => t.Value == "다른 시트").Style.Filename == "arial.ttf", "Same-name text styles keep each sheet font");
        check(merged.Layers["S-COL"].LineWeight == (LineWeightType)35, "Requested layer weight applied in final DWG");

        var inch = Sheet(); inch.Layouts.First(l => l.IsPaperSpace).PaperUnits = PlotPaperUnits.Inches;
        var inchResult = DwgReader.Read(Flatten(inch, "inch"));
        near(((Insert)inchResult.Entities.Single()).XScale, 25.4, "Inch paper converted to millimeters");
        var polygon = Sheet(); var vp = polygon.PaperSpace.Entities.OfType<Viewport>().Last();
        var boundary = Polygon(new XY(85, 92), new XY(112, 92), new XY(100, 108));
        polygon.PaperSpace.Entities.Add(boundary); vp.Boundary = boundary; vp.Status |= ViewportStatusFlags.NonRectangularClipping;
        var polygonResult = DwgReader.Read(Flatten(polygon, "polygon"));
        check(Walk(polygonResult.ModelSpace).OfType<Insert>().Single(i => i.SpatialFilter != null).SpatialFilter.BoundaryPoints.Count == 3,
            "Nonrectangular straight clipping retained");

        var tilted = Sheet(); tilted.PaperSpace.Entities.OfType<Viewport>().Last().ViewDirection = XYZ.AxisX;
        Reject(tilted, "tilted", "3차원");
        var perspective = Sheet(); perspective.PaperSpace.Entities.OfType<Viewport>().Last().Status |= ViewportStatusFlags.PerspectiveMode;
        Reject(perspective, "perspective", "원근");
        var curves = Sheet(); var curveVp = curves.PaperSpace.Entities.OfType<Viewport>().Last();
        var circle = new Circle { Center = new XYZ(100, 100, 0), Radius = 10 }; curves.PaperSpace.Entities.Add(circle);
        curveVp.Boundary = circle; curveVp.Status |= ViewportStatusFlags.NonRectangularClipping;
        Reject(curves, "curved-clip", "곡선");
        var xref = Sheet(); xref.Entities.Add(new Insert(new BlockRecord("Missing", "absent.dwg")));
        Reject(xref, "xref", "외부 참조");

        bool badGap = false;
        try { processor.Run(new BridgeRequest { Operation = "Merge", Inputs = new() { first }, MarginMm = double.NaN,
            OutputPath = Path.Combine(output, "bad-gap.dwg") }, first, output); } catch (InvalidDataException) { badGap = true; }
        check(badGap && !File.Exists(Path.Combine(output, "bad-gap.dwg")), "NaN layout margin rejected");

        var conflicting = Sheet(); conflicting.Layers["S-COL"].Color = new CadColor(5);
        string conflict = Flatten(conflicting, "layer-conflict"); bool conflictRejected = false;
        try { processor.Run(new BridgeRequest { Operation = "Merge", Inputs = new() { first, conflict },
            OutputPath = Path.Combine(output, "conflict-merge.dwg") }, first, output); } catch (InvalidDataException e) when (e.Message.Contains("레이어 설정")) { conflictRejected = true; }
        check(conflictRejected && !File.Exists(Path.Combine(output, "conflict-merge.dwg")), "Conflicting same-name layers cannot silently change colors");
        foreach (var version in new[] { ACadVersion.AC1015, ACadVersion.AC1018 })
            Reject(Sheet(version), "legacy-" + version, "2010 이상");
        foreach (var version in new[] { ACadVersion.AC1024, ACadVersion.AC1027 })
        {
            var versionResult = DwgReader.Read(Flatten(Sheet(version), "format-" + version));
            check(versionResult.Header.Version == version, "Requested DWG format retained: " + version);
            check(Walk(versionResult.ModelSpace).OfType<MText>().Any(t => t.Value == "구조 평면도 · 도곽"), "Korean text roundtrip: " + version);
        }
    }

    private static IEnumerable<Entity> Walk(BlockRecord block)
    {
        foreach (Entity e in block.Entities)
        {
            yield return e;
            if (e is Insert insert) foreach (Entity child in Walk(insert.Block)) yield return child;
        }
    }

    private static LwPolyline Polygon(params XY[] points) => new(points.Select(p => new LwPolyline.Vertex(p))) { IsClosed = true };

    private static CadDocument Sheet(ACadVersion version = ACadVersion.AC1032)
    {
        var doc = new CadDocument(version);
        doc.Header.CodePage = "ANSI_949"; // Legacy DWGs encode Korean using their declared code page.
        doc.PaperSpace.Layout.PaperUnits = PlotPaperUnits.Millimeters;
        doc.Layers.Add(new Layer("S-COL") { Color = new CadColor(3) });
        doc.Layers.Add(new Layer("FROZEN"));
        doc.Entities.Add(new Line { StartPoint = new XYZ(-1000, 40, 0), EndPoint = new XYZ(1000, 40, 0), Layer = doc.Layers["S-COL"] });
        doc.Entities.Add(new MText { Value = "기둥 C1 / 300×600", InsertPoint = new XYZ(30, 40, 0), Height = 20, RectangleWidth = 100 });
        var dim = new DimensionAligned(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { Offset = 10 };
        doc.Entities.Add(dim); dim.UpdateBlock();
        var hatch = new Hatch { IsSolid = true };
        var path = new Hatch.BoundaryPath();
        path.Edges.Add(new Hatch.BoundaryPath.Polyline(new[] { new XYZ(0, 0, 0), new XYZ(20, 0, 0), new XYZ(20, 20, 0), new XYZ(0, 20, 0) }));
        hatch.Paths.Add(path); doc.Entities.Add(hatch);
        var block = new BlockRecord("SameName");
        block.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(50, 0, 0) });
        block.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(0, 50, 0), Layer = doc.Layers["FROZEN"] });
        doc.Entities.Add(new Insert(block));
        // Paper viewport must precede model viewports in the DWG layout record.
        if (!doc.PaperSpace.Entities.OfType<Viewport>().Any()) doc.PaperSpace.Entities.Add(new Viewport { Width = 200, Height = 150, ViewHeight = 150 });
        doc.PaperSpace.Entities.Add(Polygon(new XY(0, 0), new XY(200, 0), new XY(200, 150), new XY(0, 150)));
        doc.PaperSpace.Entities.Add(new MText { Value = "구조 평면도 · 도곽", InsertPoint = new XYZ(10, 140, 0), Height = 3, RectangleWidth = 100 });
        var vp = new Viewport { Center = new XYZ(100, 100, 0), Width = 40, Height = 20, ViewHeight = 200,
            ViewCenter = new XY(10, 20), ViewTarget = new XYZ(30, 40, 0), TwistAngle = Math.PI / 2, ViewDirection = XYZ.AxisZ, ActiveStatus = 1 };
        vp.FrozenLayers.Add(doc.Layers["FROZEN"]); doc.PaperSpace.Entities.Add(vp);
        return doc;
    }
}
