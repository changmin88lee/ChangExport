using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Diagnostics;
using System.Globalization;

namespace ChangExport.DwgProcessing;

public sealed partial class ManagedDwgProcessor
{
    private const string NativeOverlayLayerPrefix = "CE$NGE$";

    /// <summary>
    /// Keeps the ordinary Revit DWG as the sole geometry source. The temporary
    /// filtered drawing is processed independently and is used only as an exact
    /// spatial classification overlay; no entity from it is copied to the result.
    /// </summary>
    private PreparedDrawing PrepareNativeGeometry(BridgeRequest request, string nativeDrawing,
        Func<bool>? cancel, Action? pump)
    {
        Action check = CreateCheck(cancel, pump);
        check();
        if (!File.Exists(request.FilterReferencePath))
            throw new FileNotFoundException("필터 분류 DWG를 찾을 수 없습니다.", request.FilterReferencePath);

        var clock = Stopwatch.StartNew();
        BridgeRequest nativeRequest = CopyRequest(request);
        nativeRequest.FilterReferencePath = "";
        nativeRequest.ColorRemaps = new();
        nativeRequest.MaterialAppearanceRemaps = new();
        nativeRequest.TextReplacements = new();
        nativeRequest.ExpectedRuleMatches = new();
        PreparedDrawing native = Prepare(nativeRequest, nativeDrawing, cancel, pump);
        check();

        var targets = new Dictionary<string, OverlayTarget>(StringComparer.OrdinalIgnoreCase);
        BridgeRequest classifierRequest = CopyRequest(request);
        classifierRequest.FilterReferencePath = "";
        classifierRequest.UseLayerColors = false;
        classifierRequest.LayerStyles = new();
        classifierRequest.ExpectedRuleMatches = new();
        classifierRequest.ColorRemaps = request.ColorRemaps.Select(map =>
        {
            string temporary = NativeOverlayLayerPrefix + "C" + map.MarkerAci.ToString(CultureInfo.InvariantCulture);
            targets[temporary] = new OverlayTarget(map.Layer, map.Color, map.RuleId,
                map.BoundaryPriority, map.RuleId.StartsWith("wrap:", StringComparison.Ordinal), map.RemapFills,
                new(map.SourceLayers));
            return new ColorLayerRemap
            {
                MarkerAci = map.MarkerAci,
                Layer = temporary,
                Color = map.Color,
                RuleId = map.RuleId,
                RemapFills = map.RemapFills,
                BoundaryPriority = map.BoundaryPriority,
                SourceLayers = new(map.SourceLayers)
            };
        }).ToList();
        classifierRequest.MaterialAppearanceRemaps = request.MaterialAppearanceRemaps.Select((map, index) =>
        {
            string temporary = NativeOverlayLayerPrefix + "A" + index.ToString(CultureInfo.InvariantCulture);
            targets[temporary] = new OverlayTarget(map.Layer, map.Color, map.RuleId,
                map.BoundaryPriority, false, true, new());
            return new MaterialAppearanceRemap
            {
                Pattern = map.Pattern,
                IsSolid = map.IsSolid,
                DisplayRgb = map.DisplayRgb,
                Layer = temporary,
                Color = map.Color,
                RuleId = map.RuleId,
                MaterialName = map.MaterialName,
                BoundaryPriority = map.BoundaryPriority,
                AllowColorOnly = map.AllowColorOnly,
                PatternLines = map.PatternLines.Select(line => new MaterialPatternLine
                {
                    SpacingMm = line.SpacingMm,
                    ShiftMm = line.ShiftMm,
                    SegmentsMm = new(line.SegmentsMm)
                }).ToList()
            };
        }).ToList();

        PreparedDrawing classified = Prepare(classifierRequest, request.FilterReferencePath, cancel, pump);
        check();
        ApplyNativeClassification(native.Document, classified.Document, targets, request, native.Response, check);
        // The native pass ran before target filter layers were created. Reapply
        // configured layer properties so type-filter linetype/weight choices are
        // identical to the legacy filtered-geometry pipeline.
        ApplyLayerStyles(native.Document, request.LayerStyles);
        native.Response.GeometrySource = "NativeGeometry";
        native.Response.FilterContainerMarkersIgnored = classified.Response.FilterContainerMarkersIgnored;
        native.Response.FilterLowerGraphicsSkipped = classified.Response.FilterLowerGraphicsSkipped;
        native.Response.TimingsMs["nativeClassificationOverlay"] = clock.Elapsed.TotalMilliseconds;
        native.Response.Warnings.Add("형상 엔진: Native Geometry Engine(NGE) · Revit 기본 DWG 형상은 유지하고 임시 필터 DWG의 분류만 정확히 일치하는 객체에 전사했습니다.");
        return native;
    }

    private static BridgeRequest CopyRequest(BridgeRequest source) => new()
    {
        Operation = source.Operation,
        OutputPath = source.OutputPath,
        Direction = source.Direction,
        MarginMm = source.MarginMm,
        Inputs = new(source.Inputs),
        LayerStyles = new(source.LayerStyles),
        RevitSheet = source.RevitSheet,
        UseLayerColors = source.UseLayerColors,
        FilterReferencePath = source.FilterReferencePath,
        ColorRemaps = new(source.ColorRemaps),
        MaterialAppearanceRemaps = new(source.MaterialAppearanceRemaps),
        TextReplacements = new(source.TextReplacements),
        ExpectedRuleMatches = new(source.ExpectedRuleMatches),
        WideLineLayers = new(source.WideLineLayers),
        FamilySources = new(source.FamilySources),
        ExcludedLayers = new(source.ExcludedLayers)
    };

    private static void ApplyNativeClassification(CadDocument native, CadDocument classified,
        IReadOnlyDictionary<string, OverlayTarget> targets, BridgeRequest request,
        BridgeResponse response, Action check)
    {
        var marks = new Dictionary<string, List<OverlayTarget>>(StringComparer.Ordinal);
        var lineMarks = new Dictionary<string, List<OverlayLine>>(StringComparer.Ordinal);
        int classifiedOccurrences = 0;
        WalkWorld(classified.ModelSpace, (entity, transform) =>
        {
            check();
            if (!targets.TryGetValue(entity.Layer.Name, out OverlayTarget? target)) return;
            string? signature = NativeSignature(entity, transform);
            if (signature == null) return;
            if (!marks.TryGetValue(signature, out List<OverlayTarget>? values))
                marks[signature] = values = new();
            values.Add(target);
            if (WorldLine(entity, transform) is { } segment)
            {
                if (!lineMarks.TryGetValue(segment.Key, out List<OverlayLine>? lines))
                    lineMarks[segment.Key] = lines = new();
                lines.Add(new OverlayLine(segment.Start, segment.End, target));
            }
            classifiedOccurrences++;
        });

        int ambiguous = 0;
        foreach (var pair in marks)
        {
            OverlayTarget? selected = ResolveTarget(pair.Value);
            if (selected == null)
            { ambiguous += pair.Value.Count; continue; }
        }

        var nativeSignatures = new HashSet<string>(StringComparer.Ordinal);
        var assignments = new Dictionary<Entity, List<OverlayTarget>>();
        WalkWorld(native.ModelSpace, (entity, transform) =>
        {
            check();
            string? signature = NativeSignature(entity, transform);
            if (signature == null) return;
            nativeSignatures.Add(signature);
            OverlayTarget? target;
            if ((!marks.TryGetValue(signature, out List<OverlayTarget>? exact)
                    || (target = ResolveTarget(exact.Where(candidate => AcceptsSourceLayer(candidate, entity.Layer.Name)))) == null)
                && (entity is not Line || WorldLine(entity, transform) is not { } nativeLine
                    || !lineMarks.TryGetValue(nativeLine.Key, out List<OverlayLine>? candidates)
                    || (target = CoveredLineTarget(nativeLine.Start, nativeLine.End, candidates, entity.Layer.Name)) == null)) return;
            if (!assignments.TryGetValue(entity, out List<OverlayTarget>? values))
                assignments[entity] = values = new();
            values.Add(target);
        });

        var counts = request.ColorRemaps.Select(map => map.RuleId)
            .Concat(request.MaterialAppearanceRemaps.Select(map => map.RuleId))
            .Distinct(StringComparer.Ordinal).ToDictionary(rule => rule, _ => 0, StringComparer.Ordinal);
        int sharedAmbiguous = 0, applied = 0;
        foreach (var pair in assignments)
        {
            OverlayTarget? selected = ResolveTarget(pair.Value);
            if (selected == null)
            { sharedAmbiguous += pair.Value.Count; continue; }
            OverlayTarget target = selected;
            if (!native.Layers.TryGetValue(target.Layer, out Layer? layer))
            {
                layer = (Layer)pair.Key.Layer.Clone();
                layer.Name = target.Layer;
                native.Layers.Add(layer);
            }
            layer.Color = new ACadSharp.Color((short)target.Color);
            pair.Key.Layer = layer;
            if (pair.Key is not Hatch) pair.Key.Color = ACadSharp.Color.ByLayer;
            counts[target.RuleId] = counts.GetValueOrDefault(target.RuleId) + 1;
            applied++;
        }

        int unmatched = marks.Where(pair => !nativeSignatures.Contains(pair.Key)).Sum(pair => pair.Value.Count);
        response.NativeOverlayMatchedEntities = applied;
        response.NativeOverlayUnmatchedMarkers = unmatched;
        response.NativeOverlayAmbiguousMarkers = ambiguous + sharedAmbiguous;
        response.CustomRuleEntityCounts = counts;
        foreach (var group in counts)
            response.Warnings.Add($"Native 필터 레이어 · 규칙 {group.Key} · 원본 DWG 객체 {group.Value:N0}개 반영");
        if (unmatched > 0)
            response.Warnings.Add($"Native 형상 보호: 임시 분류 객체 {unmatched:N0}개는 Native와 1:1 동일 형상이 아닙니다. "
                + "같은 직선의 전체 피복 조건을 통과한 선은 분류에만 사용하고, 임시 객체 자체는 최종 DWG에 추가하지 않았습니다.");
        if (response.NativeOverlayAmbiguousMarkers > 0)
            response.Warnings.Add($"Native 오분류 보호: 동일 형상에 서로 다른 필터가 겹친 판정 {response.NativeOverlayAmbiguousMarkers:N0}개는 재지정하지 않았습니다.");
        foreach (var expected in request.ExpectedRuleMatches.Where(pair => pair.Value > 0))
            if (counts.GetValueOrDefault(expected.Key) == 0)
                response.Warnings.Add($"필터 미반영 확인 필요: 규칙 {expected.Key}는 Revit 객체 {expected.Value:N0}개와 일치했지만 Native DWG의 동일 형상 객체가 없습니다.");
        if (classifiedOccurrences == 0 && request.ExpectedRuleMatches.Values.Any(value => value > 0))
            response.Warnings.Add("Native 필터 전사 확인 필요: Revit 필터 판정은 존재하지만 분류 가능한 DWG 형상 마커가 없습니다.");
    }

    private static int TargetRank(OverlayTarget target) => target.Wrapping ? int.MaxValue : target.Priority;
    private static string TargetKey(OverlayTarget target) => target.Layer + "\u001f" + target.Color + "\u001f" + target.RuleId;

    private static OverlayTarget? ResolveTarget(IEnumerable<OverlayTarget> source)
    {
        var candidates = source.GroupBy(TargetKey, StringComparer.Ordinal).Select(group => group.First()).ToList();
        if (candidates.Count == 0) return null;
        int bestRank = candidates.Max(TargetRank);
        var best = candidates.Where(candidate => TargetRank(candidate) == bestRank).ToList();
        return best.Select(TargetKey).Distinct(StringComparer.Ordinal).Count() == 1 ? best[0] : null;
    }

    private static bool AcceptsSourceLayer(OverlayTarget target, string layer) => target.SourceLayers.Count == 0
        || target.SourceLayers.Any(candidate => RevitDwgLayerNames.Equivalent(candidate, layer));

    private static OverlayTarget? CoveredLineTarget(double nativeStart, double nativeEnd, IReadOnlyList<OverlayLine> lines, string nativeLayer)
    {
        const double tolerance = 1e-5;
        var clipped = lines.Where(line => line.End > nativeStart + tolerance && line.Start < nativeEnd - tolerance)
            .Select(line => new OverlayLine(Math.Max(nativeStart, line.Start), Math.Min(nativeEnd, line.End), line.Target)).ToList();
        if (clipped.Count == 0) return null;
        var points = clipped.SelectMany(line => new[] { line.Start, line.End }).Append(nativeStart).Append(nativeEnd)
            .OrderBy(value => value).ToList();
        var distinct = new List<double>();
        foreach (double point in points)
            if (distinct.Count == 0 || Math.Abs(point - distinct[^1]) > tolerance) distinct.Add(point);
        var selected = new List<OverlayTarget>();
        for (int index = 1; index < distinct.Count; index++)
        {
            double start = distinct[index - 1], end = distinct[index];
            if (end - start <= tolerance || end <= nativeStart + tolerance || start >= nativeEnd - tolerance) continue;
            double middle = (Math.Max(start, nativeStart) + Math.Min(end, nativeEnd)) / 2;
            OverlayTarget? target = ResolveTarget(clipped.Where(line => line.Start <= middle + tolerance && line.End >= middle - tolerance
                    && AcceptsSourceLayer(line.Target, nativeLayer)).Select(line => line.Target));
            if (target == null) return null;
            selected.Add(target);
        }
        if (selected.Count == 0 || distinct.First() > nativeStart + tolerance || distinct.Last() < nativeEnd - tolerance) return null;
        OverlayTarget? result = ResolveTarget(selected);
        return result != null && selected.All(target => TargetKey(target) == TargetKey(result)) ? result : null;
    }

    private static (string Key, double Start, double End)? WorldLine(Entity source, Transform transform)
    {
        if (source is not Line) return null;
        Entity clone = (Entity)source.Clone();
        try { TransformGeometry(clone, transform); }
        catch { return null; }
        if (clone is not Line line) return null;
        double dx = line.EndPoint.X - line.StartPoint.X, dy = line.EndPoint.Y - line.StartPoint.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < Epsilon || Math.Abs(line.EndPoint.Z - line.StartPoint.Z) > 1e-5) return null;
        double x = dx / length, y = dy / length;
        if (x < -Epsilon || (Math.Abs(x) < Epsilon && y < 0)) { x = -x; y = -y; }
        double offset = -y * line.StartPoint.X + x * line.StartPoint.Y;
        double start = x * line.StartPoint.X + y * line.StartPoint.Y;
        double end = x * line.EndPoint.X + y * line.EndPoint.Y;
        static string N(double value) => Math.Round(value, 5).ToString("R", CultureInfo.InvariantCulture);
        return (N(x) + "|" + N(y) + "|" + N(offset) + "|" + N(line.StartPoint.Z), Math.Min(start, end), Math.Max(start, end));
    }

    private static void WalkWorld(BlockRecord block, Action<Entity, Transform> visit,
        Transform? transform = null, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("Native 필터 전사 중 블록 깊이가 안전 범위를 초과했습니다.");
        Transform current = transform ?? Transform.CreateTranslation(XYZ.Zero);
        foreach (Entity entity in block.GetSortedEntities())
        {
            if (entity is Insert insert)
            {
                Transform child = new(current.Matrix * InsertTransform(insert).Matrix);
                WalkWorld(insert.Block, visit, child, depth + 1);
                foreach (AttributeEntity attribute in insert.Attributes) visit(attribute, current);
            }
            else visit(entity, current);
        }
    }

    private static string? NativeSignature(Entity source, Transform transform)
    {
        Entity entity = (Entity)source.Clone();
        try { TransformGeometry(entity, transform); }
        catch { return null; }
        static string N(double value) => Math.Round(value, 6).ToString("R", CultureInfo.InvariantCulture);
        static string P(XYZ point) => N(point.X) + "," + N(point.Y) + "," + N(point.Z);
        static string Pair(XYZ first, XYZ second)
        {
            string a = P(first), b = P(second);
            return string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
        }
        if (entity is Line line) return "L|" + Pair(line.StartPoint, line.EndPoint);
        if (entity is Circle circle) return "C|" + P(circle.Center) + "|" + N(circle.Radius) + "|" + P(circle.Normal);
        if (entity is Arc arc)
        {
            double start = NormalizeAngle(arc.StartAngle), end = NormalizeAngle(arc.EndAngle);
            return "A|" + P(arc.Center) + "|" + N(arc.Radius) + "|" + N(start) + "|" + N(end) + "|" + P(arc.Normal);
        }
        if (entity is LwPolyline polyline)
        {
            string forward = string.Join(";", polyline.Vertices.Select(vertex => N(vertex.Location.X) + "," + N(vertex.Location.Y) + "," + N(vertex.Bulge)));
            string reverse = string.Join(";", polyline.Vertices.AsEnumerable().Reverse().Select(vertex => N(vertex.Location.X) + "," + N(vertex.Location.Y) + "," + N(-vertex.Bulge)));
            return "P|" + polyline.IsClosed + "|" + N(polyline.Elevation) + "|" + (string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse);
        }
        if (entity is Hatch hatch)
        {
            BoundingBox box = hatch.GetBoundingBox();
            string paths = string.Join(";", hatch.Paths.Select(path => string.Join(",", path.Edges.Select(edge => edge.Type.ToString()))));
            return "H|" + hatch.IsSolid + "|" + hatch.Paths.Count + "|" + paths + "|"
                + N(box.Min.X) + "," + N(box.Min.Y) + "," + N(box.Max.X) + "," + N(box.Max.Y);
        }
        if (entity is TextEntity text)
            return "T|" + P(text.InsertPoint) + "|" + N(text.Height) + "|" + N(text.Rotation) + "|" + text.Value;
        if (entity is MText multiline)
            return "M|" + P(multiline.InsertPoint) + "|" + N(multiline.Height) + "|" + N(multiline.Rotation) + "|" + multiline.Value;
        return null;
    }

    private static double NormalizeAngle(double value)
    {
        double result = value % (2 * Math.PI);
        return result < 0 ? result + 2 * Math.PI : result;
    }

    private sealed record OverlayTarget(string Layer, int Color, string RuleId,
        int Priority, bool Wrapping, bool RemapFills, List<string> SourceLayers);
    private sealed record OverlayLine(double Start, double End, OverlayTarget Target);
}
