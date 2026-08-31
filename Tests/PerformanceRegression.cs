using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using CSMath;

internal static class PerformanceRegression
{
    public static void Run(string output, string nativeRoot, string baseline, Action<bool, string> check)
    {
        var processor = new ManagedDwgProcessor();
        var files = Enumerable.Range(1, 2).Select(i => Path.Combine(nativeRoot, $"native_{i:000}", "sheet.dwg")).ToArray();
        var timings = new List<object>();
        // The serial file path also benefits from clone/candidate optimizations.
        for (int i = 0; i < 2; i++)
        {
            string target = Path.Combine(output, $"flat-{i + 1}.dwg"); var clock = Stopwatch.StartNew();
            processor.Run(new BridgeRequest { Operation = "Flatten", RevitSheet = true, OutputPath = target }, files[i], output);
            timings.Add(new { operation = $"Flatten-{i + 1}", ms = clock.Elapsed.TotalMilliseconds });
            Compare(Path.Combine(baseline, $"flat-{i + 1}.dwg"), target, check);
        }
        foreach (string direction in new[] { "Horizontal", "Vertical" })
        foreach (int count in new[] { 1, 2 })
        {
            var clock = Stopwatch.StartNew();
            var tasks = files.Take(count).Select(file => Task.Run(() => new ManagedDwgProcessor().Prepare(
                new BridgeRequest { RevitSheet = true }, file))).ToArray();
            Task.WaitAll(tasks);
            var drawings = tasks.Select(t => t.Result).ToArray();
            double prepareMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
            string target = Path.Combine(output, $"{direction}-{count}.dwg");
            var result = processor.MergePrepared(new BridgeRequest { RevitSheet = true, Direction = direction, OutputPath = target }, drawings, output);
            timings.Add(new { operation = $"{direction}-{count}", prepareMs, ms = clock.Elapsed.TotalMilliseconds, result.TimingsMs });
            Compare(Path.Combine(baseline, $"{direction}-{count}.dwg"), target, check);
            check(result.PaperEntityCount == 0 && result.Placements.Count == count, "Memory output retains each sheet in model space");
            try { processor.MergePrepared(new BridgeRequest { RevitSheet = true, OutputPath = target + ".duplicate" }, drawings, output); check(false, "Consumed memory input rejected"); }
            catch (InvalidOperationException) { check(true, "Consumed memory input rejected"); }
        }
        File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void CompareRuns(string baselineManifest, string currentManifest, Action<bool, string> check)
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(baselineManifest));
        using var current = JsonDocument.Parse(File.ReadAllText(currentManifest));
        var bySource = baseline.RootElement.EnumerateArray().ToDictionary(e => Path.GetFullPath(e.GetProperty("path").GetString()!),
            e => e.GetProperty("result").GetProperty("OutputPath").GetString()!, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current.RootElement.EnumerateArray())
        {
            string source = Path.GetFullPath(entry.GetProperty("path").GetString()!);
            check(bySource.ContainsKey(source), "Previously verified actual source exists");
            Compare(bySource[source], entry.GetProperty("result").GetProperty("OutputPath").GetString()!, check);
        }
    }
    internal static void Compare(string before, string after, Action<bool, string> check, bool ignoreColors = false, bool normalizePeriodicAngles = false)
    {
        var left = DwgReader.Read(before); var right = DwgReader.Read(after);
        if (normalizePeriodicAngles)
        {
            // A block transform/write can encode the same hatch endpoint as +PI or
            // -PI. Compare modulo one revolution; all other geometry stays strict.
            foreach (var entity in left.BlockRecords.Concat(right.BlockRecords).SelectMany(b => b.Entities).OfType<Hatch>())
            foreach (var arc in entity.Paths.SelectMany(p => p.Edges).OfType<Hatch.BoundaryPath.Arc>())
            {
                static double Angle(double a)
                {
                    double value = Math.Round(Math.Atan2(Math.Sin(a), Math.Cos(a)), 6);
                    return value == -Math.Round(Math.PI, 6) ? Math.Round(Math.PI, 6) : value == 0 ? 0 : value;
                }
                bool full = Math.Abs(Math.Abs(arc.EndAngle - arc.StartAngle) - 2 * Math.PI) < 1e-8;
                arc.StartAngle = Angle(arc.StartAngle); arc.EndAngle = full ? arc.StartAngle + 2 * Math.PI : Angle(arc.EndAngle);
            }
        }
        check(left.Entities.Count == right.Entities.Count, $"Entity count preserved: {Path.GetFileName(after)}");
        check(left.Header.ModelSpaceExtMin.DistanceFrom(right.Header.ModelSpaceExtMin) < 1e-5
            && left.Header.ModelSpaceExtMax.DistanceFrom(right.Header.ModelSpaceExtMax) < 1e-5, "Drawing extents preserved");
        var first = left.ModelSpace.GetSortedEntities().ToArray(); var second = right.ModelSpace.GetSortedEntities().ToArray();
        for (int i = 0; i < first.Length; i++)
        {
            string a = JsonSerializer.Serialize(Capture(first[i], 0, ignoreColors)), b = JsonSerializer.Serialize(Capture(second[i], 0, ignoreColors));
            check(a == b, $"Geometry/display/order changed at entity {i} ({first[i].ObjectName}) in {Path.GetFileName(after)}\n{a}\n{b}");
        }
    }

    // Compare graphical content, not handles, generated block names or DWG timestamps.
    private static object? Capture(object? value, int depth, bool ignoreColors = false)
    {
        if (value == null) return null;
        if (depth > 12) throw new InvalidOperationException("Unexpected deep output graph");
        if (value is double number) return double.IsFinite(number) ? Math.Round(number, 6) : number.ToString();
        if (value is float single) return Math.Round(single, 6);
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) return value.ToString();
        if (value is XYZ xyz) return new[] { Math.Round(xyz.X, 6), Math.Round(xyz.Y, 6), Math.Round(xyz.Z, 6) };
        if (value is XY xy) return new[] { Math.Round(xy.X, 6), Math.Round(xy.Y, 6) };
        if (value is IEnumerable sequence) return sequence.Cast<object?>().Select(v => Capture(v, depth + 1, ignoreColors)).ToArray();
        var result = new SortedDictionary<string, object?> { ["type"] = type.Name };
        var skip = new HashSet<string> { "Handle", "Owner", "Document", "Name", "Reactors", "XDictionary", "XData", "Block", "Style", "Material", "Layer", "LineType",
            "BoundingBox", "ObjectType", "HasDynamicSubclass", "HasXData", "HasAttributes", "CadObject", "Entities", "ShapeStyle", "PlotStyleName" };
        foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !skip.Contains(p.Name)))
        {
            var t = p.PropertyType;
            if (ignoreColors && t == typeof(ACadSharp.Color)) continue;
            if (!(t.IsPrimitive || t.IsEnum || t == typeof(string) || t.IsValueType || typeof(IEnumerable).IsAssignableFrom(t))) continue;
            result[p.Name] = Capture(p.GetValue(value), depth + 1, ignoreColors);
        }
        if (value is Entity e) { result["Layer"] = e.Layer.Name; result["LineType"] = e.LineType.Name; }
        if (value is TextEntity text) { result["Font"] = text.Style.Filename; result["BigFont"] = text.Style.BigFontFilename; }
        if (value is MText multiline) { result["Font"] = multiline.Style.Filename; result["BigFont"] = multiline.Style.BigFontFilename; }
        if (value is Dimension dimension) { result["Style"] = Capture(dimension.Style, depth + 1, ignoreColors); result["Display"] = Capture(dimension.Block?.GetSortedEntities(), depth + 1, ignoreColors); }
        if (value is Insert insert) { result["Children"] = Capture(insert.Block.GetSortedEntities(), depth + 1, ignoreColors); result["Clip"] = Capture(insert.SpatialFilter, depth + 1, ignoreColors); }
        return result;
    }
}
