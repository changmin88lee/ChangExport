using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ChangExport.DwgProcessing;
using CSMath;

internal static class ArcRotationRegression
{
    internal static void Run(string output, Action<bool, string> check, string? manifestPath, string? previousPath)
    {
        var transformGeometry = typeof(ManagedDwgProcessor).GetMethod("TransformGeometry", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (double degrees in new[] { 0d, 90, 180, 270, -90, 33 })
        foreach (double scale in new[] { 1d, 1d / 300, 300 })
        foreach (var angles in new[] { (180d, 270d), (350d, 10d), (0d, 360d), (147.4875848005836, 180d) })
        {
            var original = new Arc { Center = new XYZ(520, 15, 0), Radius = 1040,
                StartAngle = angles.Item1 * Math.PI / 180, EndAngle = angles.Item2 * Math.PI / 180 };
            var transform = new Transform(Transform.CreateTranslation(new XYZ(11665, 8325, 0)).Matrix
                * Transform.CreateRotation(XYZ.AxisZ, degrees * Math.PI / 180).Matrix
                * Transform.CreateScaling(new XYZ(scale)).Matrix);
            var actual = (Arc)original.Clone();
            transformGeometry.Invoke(null, new object[] { actual, transform });
            CheckSamples(original, actual, p => transform.ApplyTransform(p), check, $"rotation {degrees}, scale {scale}, arc {angles}");
        }
        if (manifestPath != null && previousPath != null) Real(output, manifestPath, previousPath, check);
    }

    private static XYZ Point(Arc arc, double fraction)
    {
        double sweep = arc.EndAngle - arc.StartAngle;
        while (sweep < 0) sweep += 2 * Math.PI;
        double angle = arc.StartAngle + fraction * sweep;
        return arc.Center + new XYZ(Math.Cos(angle), Math.Sin(angle), 0) * arc.Radius;
    }

    private static void CheckSamples(Arc original, Arc actual, Func<XYZ, XYZ> place, Action<bool, string> check, string label)
    {
        check(actual.Center.DistanceFrom(place(original.Center)) < 1e-5, "Arc center: " + label);
        foreach (double fraction in new[] { 0d, .25, .5, .75, 1d })
            check(Point(actual, fraction).DistanceFrom(place(Point(original, fraction))) < 1e-5,
                $"Arc sweep sample {fraction}: {label}");
    }

    private static void Real(string output, string manifestPath, string previousPath, Action<bool, string> check)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = json.RootElement;
        string root = Path.Combine(manifest.GetProperty("WorkFolder").GetString()!, "set_001");
        var sourcePaths = Directory.GetFiles(root, "*.dwg", SearchOption.AllDirectories).Append(previousPath).ToArray();
        var hashes = sourcePaths.ToDictionary(p => p, FileHash);
        var layers = JsonSerializer.Deserialize<List<LayerAppearance>>(manifest.GetProperty("layerColors").GetRawText())!;
        var wide = JsonSerializer.Deserialize<List<WideLineLayer>>(manifest.GetProperty("wideLineLayers").GetRawText())!;
        // Retain only the two definitions that actually survived in the user's v4.
        // Do not change or simulate a broader Revit family collection policy here.
        var families = new List<FamilyBlockSource> {
            new() { Identity = "actual:title", Label = "LAON_LH_Title Block_A1 - _AN_LAON 실시폼(A3)_LH 2", IsTitleBlock = true,
                NativePrefixes = new() { "LAON_LH_Title Block_A1 - _AN_LAON 실시폼_A3__LH 2-981728-" } },
            new() { Identity = "actual:bicycle", Label = "자전거보관소 - _A_5ea", NativePrefixes = new() { "자전거보관소 - _A_5ea-767472-" } }
        };
        var processor = new ManagedDwgProcessor();
        var sheets = Directory.GetFiles(root, "sheet.dwg", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
        var prepared = sheets.Select(p => processor.Prepare(new BridgeRequest { RevitSheet = true, UseLayerColors = true,
            LayerStyles = layers, WideLineLayers = wide, FamilySources = families }, p)).ToArray();
        string target = Path.Combine(output, "AA-440_swing-fixed.dwg");
        var result = processor.MergePrepared(new BridgeRequest { RevitSheet = true, UseLayerColors = true,
            LayerStyles = layers, OutputPath = target }, prepared, output);
        var corrected = DwgReader.Read(target); var previous = ReadShared(previousPath);
        check(result.Success && result.PaperEntityCount == 0, "Actual set saved in model space");
        check(result.FamilyBlockReferences == 3 && result.FamilyBlockDefinitions == 2, "Existing title/bicycle policy unchanged");
        check(corrected.Entities.Count == previous.Entities.Count, "Actual v4 entity count unchanged");
        check(corrected.Entities.OfType<LwPolyline>().Count(p => Math.Abs(p.ConstantWidth - 240) < 1e-6) == 101, "101 existing 240 mm wide lines retained");
        check(corrected.Layers.All(l => l.Color.Index == 7)
            && corrected.BlockRecords.SelectMany(b => b.Entities).Where(e => e is not (Hatch or Solid or Wipeout)).All(e => e.Color.IsByLayer),
            "Layer and non-fill entity colors retained while Revit fills keep explicit view colors");
        var diagnostics = new List<object>(); int swings = 0, previouslyWrong = 0;
        for (int sheetIndex = 0; sheetIndex < sheets.Length; sheetIndex++)
        {
            var sheet = DwgReader.Read(sheets[sheetIndex]);
            var views = sheet.Layouts.Where(l => l.IsPaperSpace).SelectMany(l => l.AssociatedBlock.Entities).OfType<Viewport>()
                .Where(v => Math.Abs(v.ScaleFactor - 1d / 300) < 1e-8).ToArray();
            foreach (var reference in sheet.Entities.OfType<Insert>())
            {
                string nativePath = Path.Combine(Path.GetDirectoryName(sheets[sheetIndex])!, Path.GetFileName(reference.Block.BlockEntity.XRefPath));
                if (!File.Exists(nativePath)) continue;
                var native = DwgReader.Read(nativePath);
                // The view's center follows the corresponding horizontally staged Xref.
                var view = views.OrderBy(v => Math.Abs(v.ViewCenter.X - reference.InsertPoint.X - 12212.616062351892)).First();
                check(Math.Abs(view.TwistAngle) < 1e-8 && reference.Rotation == 0 && reference.XScale == 1, "Known actual fixture view placement");
                foreach (var insert in native.Entities.OfType<Insert>().Where(i => i.Block.Name.StartsWith("외여닫이문")
                    || i.Block.Name.StartsWith("세대현관문") || i.Block.Name.StartsWith("점검구_")))
                foreach (var arc in insert.Block.Entities.OfType<Arc>())
                {
                    check(arc.Normal == XYZ.AxisZ && insert.Normal == XYZ.AxisZ && insert.XScale == 1 && insert.YScale == 1,
                        "Native door fixture has planar unit placement");
                    XYZ Place(XYZ p)
                    {
                        p -= insert.Block.BlockEntity.BasePoint;
                        double c = Math.Cos(insert.Rotation), s = Math.Sin(insert.Rotation);
                        p = new XYZ(c * p.X - s * p.Y, s * p.X + c * p.Y, p.Z) + insert.InsertPoint + reference.InsertPoint;
                        return new XYZ(p.X - view.ViewTarget.X - view.ViewCenter.X + 300 * view.Center.X + result.Placements[sheetIndex].X,
                            p.Y - view.ViewTarget.Y - view.ViewCenter.Y + 300 * view.Center.Y + result.Placements[sheetIndex].Y, 0);
                    }
                    XYZ center = Place(arc.Center);
                    var actual = corrected.Entities.OfType<Arc>().Single(a => a.Center.DistanceFrom(center) < 1e-5 && Math.Abs(a.Radius - arc.Radius) < 1e-5);
                    CheckSamples(arc, actual, Place, check, insert.Block.Name);
                    var old = previous.Entities.OfType<Arc>().Single(a => a.Center.DistanceFrom(center) < 1e-5 && Math.Abs(a.Radius - arc.Radius) < 1e-5);
                    double oldError = new[] { 0d, .5, 1d }.Max(t => Point(old, t).DistanceFrom(Place(Point(arc, t))));
                    if (oldError > 1e-5) previouslyWrong++;
                    swings++; diagnostics.Add(new { view = Path.GetFileName(nativePath), block = insert.Block.Name, center,
                        radius = arc.Radius, beforeStart = old.StartAngle, beforeEnd = old.EndAngle,
                        afterStart = actual.StartAngle, afterEnd = actual.EndAngle, oldErrorMm = oldError });
                }
            }
        }
        check(swings >= 20 && previouslyWrong > 0, "Real door swings checked, including previously incorrect rotations");
        var capture = typeof(PerformanceRegression).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!;
        string UnchangedProperties(Entity entity)
        {
            var snapshot = JsonSerializer.SerializeToNode(capture.Invoke(null, new object[] { entity, 0, false }))!;
            void Visit(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    // Angles are the approved correction. Centers, radii, all other
                    // entities, display properties and draw order must stay unchanged.
                    if (obj["type"]?.GetValue<string>() == nameof(Arc))
                    { obj.Remove("StartAngle"); obj.Remove("EndAngle"); obj.Remove("Sweep"); }
                    foreach (var property in obj) Visit(property.Value);
                }
                else if (node is JsonArray array) foreach (var child in array) Visit(child);
            }
            Visit(snapshot); return snapshot.ToJsonString();
        }
        var oldOrder = previous.ModelSpace.GetSortedEntities().ToArray();
        var newOrder = corrected.ModelSpace.GetSortedEntities().ToArray();
        for (int i = 0; i < oldOrder.Length; i++)
            check(UnchangedProperties(oldOrder[i]) == UnchangedProperties(newOrder[i]), $"Only arc angles may change: entity {i}, {oldOrder[i].ObjectName}");
        check(hashes.All(p => FileHash(p.Key) == p.Value), "All original DWGs unchanged");
        File.WriteAllText(Path.Combine(output, "swing-review.json"), JsonSerializer.Serialize(new { swings, previouslyWrong, result, diagnostics }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FileHash(string path)
    { using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return Convert.ToHexString(SHA256.HashData(file)); }
    private static CadDocument ReadShared(string path)
    { using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return DwgReader.Read(file); }
}
