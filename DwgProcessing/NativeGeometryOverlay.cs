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
        WalkWorld(classified.ModelSpace, (entity, transform, _) =>
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
        var nativeHatches = new Dictionary<string, List<Hatch>>(StringComparer.Ordinal);
        var nativeHatchOccurrences = new List<(Hatch Entity, string Signature)>();
        var assignments = new Dictionary<Entity, List<OverlayTarget>>();
        var lineOccurrences = new Dictionary<Line, List<List<OverlayPiece>>>(ReferenceEqualityComparer.Instance);
        var lineOwners = new Dictionary<Line, BlockRecord>(ReferenceEqualityComparer.Instance);
        WalkWorld(native.ModelSpace, (entity, transform, owner) =>
        {
            check();
            string? signature = NativeSignature(entity, transform);
            if (signature == null) return;
            nativeSignatures.Add(signature);
            if (entity is Hatch nativeHatch)
            {
                if (!nativeHatches.TryGetValue(signature, out List<Hatch>? hatchValues))
                    nativeHatches[signature] = hatchValues = new();
                hatchValues.Add(nativeHatch);
                nativeHatchOccurrences.Add((nativeHatch, signature));
                return;
            }
            if (entity is Line nativeEntity && WorldLine(entity, transform) is { } nativeLine)
            {
                IReadOnlyList<OverlayLine> candidates = lineMarks.TryGetValue(nativeLine.Key, out List<OverlayLine>? lineValues)
                    ? lineValues : Array.Empty<OverlayLine>();
                if (!lineOccurrences.TryGetValue(nativeEntity, out List<List<OverlayPiece>>? occurrences))
                    lineOccurrences[nativeEntity] = occurrences = new();
                occurrences.Add(CoveredLinePieces(nativeLine, candidates, entity.Layer.Name));
                lineOwners.TryAdd(nativeEntity, owner);
                return;
            }
            OverlayTarget? target;
            if (!marks.TryGetValue(signature, out List<OverlayTarget>? exact)
                || (target = ResolveTarget(exact.Where(candidate => AcceptsSourceLayer(candidate, entity.Layer.Name)))) == null) return;
            if (!assignments.TryGetValue(entity, out List<OverlayTarget>? values))
                assignments[entity] = values = new();
            values.Add(target);
        });

        // A bounding-envelope hatch key can silently transfer a material to a
        // different hatch with the same extents. Hatch classification therefore
        // requires a unique full-boundary signature and equal marker/native
        // cardinality. Uncertainty is a hard failure, never a guessed assignment.
        var hatchTargets = new Dictionary<string, OverlayTarget>(StringComparer.Ordinal);
        foreach (var pair in marks.Where(pair => IsHatchSignature(pair.Key)))
        {
            OverlayTarget? target = ResolveTarget(pair.Value);
            if (target == null)
                throw HatchMatchFailure("동일 해치 형상에 서로 다른 우선 필터가 겹쳤습니다.", pair.Key,
                    pair.Value.Count, nativeHatches.GetValueOrDefault(pair.Key)?.Count ?? 0);
            int markerCount = pair.Value.Count(candidate => TargetKey(candidate) == TargetKey(target));
            List<Hatch> eligible = nativeHatches.GetValueOrDefault(pair.Key)?
                .Where(hatch => AcceptsSourceLayer(target, hatch.Layer.Name)).ToList() ?? new();
            if (markerCount != eligible.Count)
                throw HatchMatchFailure("분류 해치와 Native 해치의 개수가 달라 대상을 확정할 수 없습니다.", pair.Key,
                    markerCount, eligible.Count);
            hatchTargets[pair.Key] = target;
            foreach (Hatch hatch in eligible)
            {
                if (!assignments.TryGetValue(hatch, out List<OverlayTarget>? values))
                    assignments[hatch] = values = new();
                values.Add(target);
            }
        }
        foreach (var shared in nativeHatchOccurrences.GroupBy(occurrence => occurrence.Entity, ReferenceEqualityComparer.Instance))
        {
            var occurrenceTargets = shared.Select(occurrence => hatchTargets.TryGetValue(occurrence.Signature, out OverlayTarget? target)
                    && AcceptsSourceLayer(target, occurrence.Entity.Layer.Name) ? TargetKey(target) : null).ToArray();
            if (occurrenceTargets.Any(target => target != null)
                && (occurrenceTargets.Any(target => target == null)
                    || occurrenceTargets.Where(target => target != null).Distinct(StringComparer.Ordinal).Count() > 1))
                throw HatchMatchFailure("공유 블록 정의의 일부 배치에만 필터가 일치하여 개별 재지정을 안전하게 적용할 수 없습니다.",
                     string.Join(" / ", shared.Select(occurrence => occurrence.Signature).Distinct(StringComparer.Ordinal)),
                     occurrenceTargets.Count(target => target != null), occurrenceTargets.Length);
        }

        int sharedAmbiguous = 0, partialLinesSplit = 0;
        var lineReplacements = new Dictionary<Line, List<Line>>(ReferenceEqualityComparer.Instance);
        foreach (var pair in lineOccurrences)
        {
            List<List<OverlayPiece>> occurrences = pair.Value;
            var variants = occurrences.Select(PieceKey).Distinct(StringComparer.Ordinal).ToList();
            if (variants.Count != 1)
            {
                sharedAmbiguous += occurrences.Count;
                continue;
            }
            List<OverlayPiece> pieces = occurrences[0];
            if (!pieces.Any(piece => piece.Target != null)) continue;
            if (pieces.Count == 1 && pieces[0].Target is { } whole)
            {
                assignments[pair.Key] = new() { whole };
                continue;
            }
            if (!lineOwners.ContainsKey(pair.Key))
            {
                sharedAmbiguous += occurrences.Count;
                continue;
            }
            XYZ originalStart = pair.Key.StartPoint, originalEnd = pair.Key.EndPoint;
            var replacements = new List<Line>();
            foreach (OverlayPiece piece in pieces)
            {
                if (piece.End - piece.Start <= 1e-9) continue;
                var clone = (Line)pair.Key.Clone();
                clone.StartPoint = originalStart + (originalEnd - originalStart) * piece.Start;
                clone.EndPoint = originalStart + (originalEnd - originalStart) * piece.End;
                replacements.Add(clone);
                if (piece.Target != null) assignments[clone] = new() { piece.Target };
            }
            lineReplacements[pair.Key] = replacements;
            partialLinesSplit++;
        }
        foreach (var ownerGroup in lineReplacements.GroupBy(pair => lineOwners[pair.Key]))
        {
            BlockRecord owner = ownerGroup.Key;
            Entity[] ordered = owner.GetSortedEntities().ToArray();
            var replacements = new Dictionary<Line, List<Line>>(ReferenceEqualityComparer.Instance);
            foreach (var pair in ownerGroup) replacements[pair.Key] = pair.Value;
            owner.Entities.Clear();
            var rebuilt = new List<Entity>();
            foreach (Entity entity in ordered)
            {
                if (entity is Line original && replacements.TryGetValue(original, out List<Line>? pieces))
                {
                    foreach (Line piece in pieces) { owner.Entities.Add(piece); rebuilt.Add(piece); }
                }
                else { owner.Entities.Add(entity); rebuilt.Add(entity); }
            }
            PreserveMaskDrawOrder(owner, rebuilt);
        }

        var counts = request.ColorRemaps.Select(map => map.RuleId)
            .Concat(request.MaterialAppearanceRemaps.Select(map => map.RuleId))
            .Distinct(StringComparer.Ordinal).ToDictionary(rule => rule, _ => 0, StringComparer.Ordinal);
        int applied = 0;
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
        response.NativeOverlayPartialLinesSplit = partialLinesSplit;
        response.CustomRuleEntityCounts = counts;
        foreach (var group in counts)
            response.Warnings.Add($"Native 필터 레이어 · 규칙 {group.Key} · 원본 DWG 객체 {group.Value:N0}개 반영");
        if (unmatched > 0)
            response.Warnings.Add($"Native 형상 보호: 임시 분류 객체 {unmatched:N0}개는 Native와 1:1 동일 형상이 아닙니다. "
                + "같은 직선에서 명확히 일치하는 구간은 분류에만 사용하고, 임시 객체 자체는 최종 DWG에 추가하지 않았습니다.");
        if (partialLinesSplit > 0)
            response.Warnings.Add($"Native 부분 재료 경계: Part와 명확히 일치하는 구간만 반영하기 위해 원본 직선 {partialLinesSplit:N0}개를 안전하게 분할했습니다.");
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
    private static bool IsHatchSignature(string signature) => signature.StartsWith("H|", StringComparison.Ordinal);

    private static InvalidDataException HatchMatchFailure(string reason, string signature, int markers, int natives) =>
        new("Native 해치 1:1 매칭 실패: " + reason + $" 분류 {markers:N0}개 · Native {natives:N0}개 · 서명 {signature}");

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

    private static List<OverlayPiece> CoveredLinePieces(OverlayWorldLine native, IReadOnlyList<OverlayLine> lines, string nativeLayer)
    {
        const double tolerance = 1e-5;
        var clipped = lines.Where(line => line.End > native.Start + tolerance && line.Start < native.End - tolerance)
            .Select(line => new OverlayLine(Math.Max(native.Start, line.Start), Math.Min(native.End, line.End), line.Target)).ToList();
        if (clipped.Count == 0) return new() { new OverlayPiece(0, 1, null) };
        var points = clipped.SelectMany(line => new[] { line.Start, line.End }).Append(native.Start).Append(native.End)
            .OrderBy(value => value).ToList();
        var distinct = new List<double>();
        foreach (double point in points)
            if (distinct.Count == 0 || Math.Abs(point - distinct[^1]) > tolerance) distinct.Add(point);
        var pieces = new List<OverlayPiece>();
        double length = native.End - native.Start;
        double Parameter(double value) => native.Forward
            ? (value - native.Start) / length
            : (native.End - value) / length;
        for (int index = 1; index < distinct.Count; index++)
        {
            double start = distinct[index - 1], end = distinct[index];
            if (end - start <= tolerance || end <= native.Start + tolerance || start >= native.End - tolerance) continue;
            start = Math.Max(start, native.Start); end = Math.Min(end, native.End);
            double middle = (start + end) / 2;
            OverlayTarget? target = ResolveTarget(clipped.Where(line => line.Start <= middle + tolerance && line.End >= middle - tolerance
                    && AcceptsSourceLayer(line.Target, nativeLayer)).Select(line => line.Target));
            double first = Parameter(start), second = Parameter(end);
            pieces.Add(new OverlayPiece(Math.Min(first, second), Math.Max(first, second), target));
        }
        if (pieces.Count == 0) return new() { new OverlayPiece(0, 1, null) };
        bool divided = pieces.Any(piece => piece.Target == null)
            || pieces.Where(piece => piece.Target != null).Select(piece => TargetKey(piece.Target!))
                .Distinct(StringComparer.Ordinal).Skip(1).Any();
        if (divided)
            pieces = pieces.Select(piece => piece.Target is { RemapFills: false, Wrapping: false }
                ? piece with { Target = null } : piece).ToList();
        var merged = new List<OverlayPiece>();
        foreach (OverlayPiece piece in pieces.OrderBy(piece => piece.Start))
        {
            if (merged.Count > 0 && Math.Abs(merged[^1].End - piece.Start) <= 1e-9
                && SameTarget(merged[^1].Target, piece.Target))
                merged[^1] = merged[^1] with { End = piece.End };
            else merged.Add(piece);
        }
        return merged;
    }

    private static bool SameTarget(OverlayTarget? left, OverlayTarget? right) => left == null || right == null
        ? left == null && right == null
        : TargetKey(left) == TargetKey(right);

    private static string PieceKey(IEnumerable<OverlayPiece> pieces) => string.Join("|", pieces.Select(piece =>
        Math.Round(piece.Start, 8).ToString("R", CultureInfo.InvariantCulture) + ":"
        + Math.Round(piece.End, 8).ToString("R", CultureInfo.InvariantCulture) + ":"
        + (piece.Target == null ? "-" : TargetKey(piece.Target))));

    private static OverlayWorldLine? WorldLine(Entity source, Transform transform)
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
        double first = x * line.StartPoint.X + y * line.StartPoint.Y;
        double second = x * line.EndPoint.X + y * line.EndPoint.Y;
        static string N(double value) => Math.Round(value, 5).ToString("R", CultureInfo.InvariantCulture);
        return new OverlayWorldLine(N(x) + "|" + N(y) + "|" + N(offset) + "|" + N(line.StartPoint.Z),
            Math.Min(first, second), Math.Max(first, second), first <= second);
    }

    private static void WalkWorld(BlockRecord block, Action<Entity, Transform, BlockRecord> visit,
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
                foreach (AttributeEntity attribute in insert.Attributes) visit(attribute, current, block);
            }
            else visit(entity, current, block);
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
            return HatchSignature(hatch);
        if (entity is TextEntity text)
            return "T|" + P(text.InsertPoint) + "|" + N(text.Height) + "|" + N(text.Rotation) + "|" + text.Value;
        if (entity is MText multiline)
            return "M|" + P(multiline.InsertPoint) + "|" + N(multiline.Height) + "|" + N(multiline.Rotation) + "|" + multiline.Value;
        return null;
    }

    private static string HatchSignature(Hatch hatch)
    {
        static string N(double value)
        {
            if (value == 0) value = 0; // normalize negative zero
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
        static string P2(XY point) => N(point.X) + "," + N(point.Y);
        static string P3(XYZ point) => N(point.X) + "," + N(point.Y) + "," + N(point.Z);
        static string Values(IEnumerable<double> values) => string.Join(",", values.Select(N));
        static string Points(IEnumerable<XY> values) => string.Join(";", values.Select(P2));
        static string Points3(IEnumerable<XYZ> values) => string.Join(";", values.Select(P3));
        static string Edge(Hatch.BoundaryPath.Edge edge) => edge switch
        {
            Hatch.BoundaryPath.Line line => "L:" + P2(line.Start) + ">" + P2(line.End),
            Hatch.BoundaryPath.Arc arc => "A:" + P2(arc.Center) + "," + N(arc.Radius) + ","
                + N(arc.StartAngle) + "," + N(arc.EndAngle) + "," + arc.CounterClockWise,
            Hatch.BoundaryPath.Ellipse ellipse => "E:" + P2(ellipse.Center) + "," + P2(ellipse.MajorAxisEndPoint)
                + "," + N(ellipse.MajorAxis) + "," + N(ellipse.MinorAxis) + "," + N(ellipse.RadiusRatio)
                + "," + N(ellipse.Rotation) + "," + N(ellipse.StartAngle) + "," + N(ellipse.EndAngle)
                + "," + ellipse.CounterClockWise,
            Hatch.BoundaryPath.Polyline polyline => "P:" + polyline.IsClosed + "," + polyline.HasBulge + ","
                + Points3(polyline.Vertices) + "|" + Values(polyline.Bulges),
            Hatch.BoundaryPath.Spline spline => "S:" + spline.Degree + "," + spline.IsRational + "," + spline.IsPeriodic
                + "|C:" + Points3(spline.ControlPoints) + "|K:" + Values(spline.Knots) + "|W:" + Values(spline.Weights)
                + "|F:" + Points(spline.FitPoints) + "|T:" + P2(spline.StartTangent) + ">" + P2(spline.EndTangent),
            _ => "U:" + edge.Type
        };
        static string Token(string? value) => (value?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + ":" + value;

        string paths = string.Join("||", hatch.Paths.Select(path => ((int)path.Flags).ToString(CultureInfo.InvariantCulture)
            + ":" + path.IsPolyline + ":" + string.Join("|", path.Edges.Select(Edge))));
        string pattern = hatch.Pattern == null ? "-" : Token(hatch.Pattern.Name) + "|" + Token(hatch.Pattern.Description)
            + "|" + string.Join(";", hatch.Pattern.Lines.Select(line => N(line.Angle) + "," + P2(line.BasePoint)
                + "," + P2(line.Offset) + "," + Values(line.DashLengths)));
        return "H|" + hatch.IsSolid + "|" + hatch.IsDouble + "|" + (int)hatch.PatternType + "|" + (int)hatch.Style
            + "|" + N(hatch.Elevation) + "|" + P3(hatch.Normal) + "|" + N(hatch.PatternAngle) + "|"
            + N(hatch.PatternScale) + "|" + N(hatch.PixelSize) + "|" + Token(pattern) + "|" + Token(paths);
    }

    private static double NormalizeAngle(double value)
    {
        double result = value % (2 * Math.PI);
        return result < 0 ? result + 2 * Math.PI : result;
    }

    private sealed record OverlayTarget(string Layer, int Color, string RuleId,
        int Priority, bool Wrapping, bool RemapFills, List<string> SourceLayers);
    private sealed record OverlayLine(double Start, double End, OverlayTarget Target);
    private sealed record OverlayWorldLine(string Key, double Start, double End, bool Forward);
    private sealed record OverlayPiece(double Start, double End, OverlayTarget? Target);
}
