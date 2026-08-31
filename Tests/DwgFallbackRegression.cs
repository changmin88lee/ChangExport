using System.Security.Cryptography;
using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Types;
using ChangExport.DwgProcessing;
using CSMath;

internal static class DwgFallbackRegression
{
    public static void Run(string output, Action<bool, string> check, Action<double, double, string> near)
    {
        var processor = new ManagedDwgProcessor();
        (CadDocument doc, BridgeResponse result) Flatten(CadDocument source, string name)
        {
            string input = Path.Combine(output, name + "-source.dwg"), final = Path.Combine(output, name + ".dwg");
            var issues = new List<string>();
            DwgWriter.Write(input, source, notification: (_, e) => { if (e.NotificationType != NotificationType.None) issues.Add(e.Message); });
            check(issues.Count == 0, "Fixture writer did not drop content: " + name + " " + string.Join(" / ", issues));
            byte[] hash = SHA256.HashData(File.ReadAllBytes(input));
            var result = processor.Run(new BridgeRequest { Operation = "Flatten", OutputPath = final }, input, output);
            check(result.Success && File.Exists(final) && result.PaperEntityCount == 0, "Final model-space file saved: " + name);
            check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input))), "Original image/view DWG unchanged: " + name);
            return (DwgReader.Read(final), result);
        }

        // Include a good viewport and two omitted viewports in the same source DWG.
        var mixed = DwgRegression.Sheet(ACadVersion.AC1024);
        var perspective = (Viewport)mixed.PaperSpace.Entities.OfType<Viewport>().Last().Clone();
        perspective.FrozenLayers.Clear(); perspective.FrozenLayers.Add(mixed.Layers["FROZEN"]);
        perspective.Center = new XYZ(45, 40, 0); perspective.Status |= ViewportStatusFlags.PerspectiveMode;
        perspective.ViewDirection = new XYZ(1, 1, 1); mixed.PaperSpace.Entities.Add(perspective);
        var shaded = (Viewport)perspective.Clone(); shaded.Status &= ~ViewportStatusFlags.PerspectiveMode;
        shaded.FrozenLayers.Clear(); shaded.FrozenLayers.Add(mixed.Layers["FROZEN"]);
        shaded.RenderMode = RenderMode.FlatShaded; shaded.Center = new XYZ(145, 40, 0); mixed.PaperSpace.Entities.Add(shaded);
        var (mixedDoc, mixedResult) = Flatten(mixed, "mixed-views-2010");
        check(mixedResult.Warnings.Count(w => w.StartsWith("생략:")) == 2, "Both omitted viewports reported");
        var mixedEntities = DwgRegression.Walk(mixedDoc.ModelSpace).ToList();
        check(mixedEntities.OfType<Insert>().Count(i => i.SpatialFilter != null) == 1, "Good viewport survives perspective and shade on same sheet");
        check(mixedEntities.OfType<MText>().Any(t => t.Value == "구조 평면도 · 도곽") && mixedEntities.OfType<MText>().Any(t => t.Value == "기둥 C1 / 300×600"),
            "Paper annotation and good model view both survive");
        check(mixedEntities.OfType<Dimension>().Any() && mixedEntities.OfType<Hatch>().Any(), "Good dimensions and hatch survive");

        // The SDK cannot write an ACIS source fixture. Exercise the post-read
        // conversion directly, then write/reopen its actual DWG output.
        mixed.Entities.Add(new Solid3D());
        mixed.Entities.OfType<Insert>().Single().Block.Entities.Add(new Solid3D());
        var geometryWarnings = new List<string>();
        var converted = (CadDocument)typeof(ManagedDwgProcessor).GetMethod("Flatten", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { mixed, geometryWarnings, (Action)(() => { }) })!;
        string geometryPath = Path.Combine(output, "shared-3d-geometry.dwg"); DwgWriter.Write(geometryPath, converted);
        var remaining = DwgRegression.Walk(DwgReader.Read(geometryPath).ModelSpace).ToList();
        check(!remaining.OfType<ModelerGeometry>().Any() && remaining.OfType<Line>().Any() && remaining.OfType<Dimension>().Any(),
            "Shaded view's shared top-level/nested 3D solids cannot abort remaining 2D output");
        check(geometryWarnings.Any(w => w.Contains("3D 표현 객체")), "Shared 3D omissions are disclosed");

        foreach (ShadePlotMode mode in new[] { ShadePlotMode.Hidden, ShadePlotMode.Rendered })
        {
            var doc = DwgRegression.Sheet(ACadVersion.AC1024);
            doc.PaperSpace.Entities.OfType<Viewport>().Last().ShadePlotMode = mode;
            var (saved, response) = Flatten(doc, "shade-plot-" + mode);
            check(response.Warnings.Any(w => w.StartsWith("생략:")) && DwgRegression.Walk(saved.ModelSpace).OfType<MText>().Count() == 1,
                "Shade plot setting does not abort paper output: " + mode);
        }

        // Missing bitmap files do not matter: placement comes from DWG metadata.
        var images = DwgRegression.Sheet(ACadVersion.AC1024);
        var image = Image("없는_로고.png", new XYZ(20, 30, 0)); image.Layer = images.Layers["S-COL"]; image.Color = new ACadSharp.Color(5);
        image.LineWeight = (LineWeightType)35;
        images.PaperSpace.Entities.Add(image);
        images.Entities.Add(Image("모델_이미지.png", new XYZ(30, 40, 0)));
        var nested = new BlockRecord("ImageBlock"); nested.Entities.Add(Image("중첩_이미지.png", XYZ.Zero));
        nested.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(7, 0, 0) });
        images.PaperSpace.Entities.Add(new Insert(nested) { InsertPoint = new XYZ(120, 70, 0), Rotation = Math.PI / 2, XScale = 2, YScale = 3 });
        var (imageDoc, imageResult) = Flatten(images, "images-2010");
        var imageEntities = DwgRegression.Walk(imageDoc.ModelSpace).ToList();
        check(!imageEntities.OfType<RasterImage>().Any() && imageEntities.OfType<Polyline3D>().Count() == 3, "Paper, model and nested images replaced without bitmap references");
        check(imageResult.Warnings.Count(w => w.StartsWith("대체:")) == 3, "Each image replacement reported");
        var paper = ((Insert)imageDoc.Entities.Single()).Block;
        var box = paper.Entities.OfType<Polyline3D>().Single();
        check(box.IsClosed && box.Vertices.Count == 4 && box.Layer.Name == "S-COL" && box.Color.Index == 5 && box.LineWeight == (LineWeightType)35,
            "Image rectangle is closed and preserves appearance");
        XYZ[] corners = { new(20, 30, 0), new(20, 80, 0), new(0, 80, 0), new(0, 30, 0) };
        int n = 0;
        foreach (var vertex in box.Vertices)
        { near(vertex.Location.X, corners[n].X, "Rotated pixel vector X"); near(vertex.Location.Y, corners[n++].Y, "Rotated pixel vector Y"); }
        var nestedInsert = paper.Entities.OfType<Insert>().Single(i => i.SpatialFilter == null);
        var nestedBox = nestedInsert.Block.Entities.OfType<Polyline3D>().Single();
        XYZ nestedCorner = nestedInsert.GetTransform().ApplyTransform(nestedBox.Vertices.ElementAt(2).Location);
        near(nestedCorner.X, -30, "Nested rotated/scaled image X"); near(nestedCorner.Y, 30, "Nested rotated/scaled image Y");
        check(nestedInsert.Block.GetSortedEntities().First() is Polyline3D && nestedInsert.Block.GetSortedEntities().Last() is Line,
            "Replacement retains block entity drawing order: " + string.Join(",", nestedInsert.Block.GetSortedEntities().Select(e => e.ObjectName + ":" + e.Handle)));
        DxfWriter.Write(Path.Combine(output, "images-crosscheck.dxf"), imageDoc);

        // A sheet containing only an omitted viewport must keep its set position.
        var empty = DwgRegression.Sheet(ACadVersion.AC1024);
        foreach (var e in empty.PaperSpace.Entities.Where(e => e is not Viewport).ToArray()) empty.PaperSpace.Entities.Remove(e);
        empty.PaperSpace.Entities.OfType<Viewport>().Last().Status |= ViewportStatusFlags.PerspectiveMode;
        var (emptyDoc, emptyResult) = Flatten(empty, "only-perspective-2010");
        check(DwgRegression.Walk(emptyDoc.ModelSpace).OfType<LwPolyline>().Single().IsClosed && emptyResult.Warnings.Any(w => w.Contains("자리를 유지")),
            "Otherwise empty sheet keeps a documented outline and set slot");
        near(emptyResult.EntityBounds.Single().Width, 40, "Omitted-only sheet width preserved");
        near(emptyResult.EntityBounds.Single().Height, 20, "Omitted-only sheet height preserved");

        var onlyImage = DwgRegression.Sheet(ACadVersion.AC1024);
        foreach (var e in onlyImage.PaperSpace.Entities.Where(e => e is not Viewport v || !v.RepresentsPaper).ToArray()) onlyImage.PaperSpace.Entities.Remove(e);
        onlyImage.PaperSpace.Entities.Add(Image("image-only.png", new XYZ(20, 30, 0)));
        var (_, onlyImageResult) = Flatten(onlyImage, "only-image-2010");
        near(onlyImageResult.EntityBounds.Single().Width, 20, "Image-only sheet real width");
        near(onlyImageResult.EntityBounds.Single().Height, 50, "Image-only sheet real height");

        foreach (string direction in new[] { "Horizontal", "Vertical" })
        {
            string final = Path.Combine(output, "fallback-set-" + direction + ".dwg");
            var merged = processor.Run(new BridgeRequest { Operation = "Merge", OutputPath = final, Direction = direction, MarginMm = 25,
                Inputs = new() { mixedResult.OutputPath, emptyResult.OutputPath, imageResult.OutputPath, onlyImageResult.OutputPath } }, mixedResult.OutputPath, output);
            var actual = DwgReader.Read(final);
            check(merged.Success && actual.Entities.Count == 4 && merged.Placements.Count == 4 && actual.Header.Version == ACadVersion.AC1024,
                "All four mixed-content sheets saved as DWG 2010: " + direction);
            for (int i = 1; i < merged.EntityBounds.Count; i++)
            {
                var previous = merged.EntityBounds[i - 1]; var current = merged.EntityBounds[i];
                near(direction == "Horizontal" ? current.X - previous.X - previous.Width : previous.Y - current.Y - current.Height, 25,
                    "Mixed-content set maintains requested gap: " + direction);
            }
            var all = DwgRegression.Walk(actual.ModelSpace).ToList();
            check(all.OfType<Polyline3D>().Count() == 4 && all.OfType<MText>().Count(t => t.Value == "구조 평면도 · 도곽") == 2,
                "Other sheet contents survive final merge: " + direction);
            DxfWriter.Write(Path.Combine(output, "fallback-set-" + direction + "-crosscheck.dxf"), actual);
        }
    }

    private static RasterImage Image(string name, XYZ point)
    {
        var image = new RasterImage(new ImageDefinition { Name = Path.GetFileNameWithoutExtension(name), FileName = name, Size = new XY(100, 50) })
        { InsertPoint = point, UVector = new XYZ(0, 0.5, 0), VVector = new XYZ(-0.4, 0, 0), Size = new XY(100, 50), ShowImage = true };
        image.ClipBoundaryVertices.Add(new XY(-0.5, -0.5)); image.ClipBoundaryVertices.Add(new XY(99.5, 49.5));
        return image;
    }
}
