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
                new(map.SourceLayers), map.MarkerAci, map.PreserveNative);
            return new ColorLayerRemap
            {
                MarkerAci = map.MarkerAci,
                Layer = temporary,
                Color = map.Color,
                RuleId = map.RuleId,
                RemapFills = map.RemapFills,
                BoundaryPriority = map.BoundaryPriority,
                SourceLayers = new(map.SourceLayers),
                PreserveNative = map.PreserveNative
            };
        }).ToList();
        classifierRequest.MaterialAppearanceRemaps = request.MaterialAppearanceRemaps.Select((map, index) =>
        {
            string temporary = NativeOverlayLayerPrefix + "A" + index.ToString(CultureInfo.InvariantCulture);
            targets[temporary] = new OverlayTarget(map.Layer, map.Color, map.RuleId,
                map.BoundaryPriority, false, true, new(), -(index + 1), false);
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
        var diagnostics = request.ColorRemaps.ToDictionary(map => map.MarkerAci, map => new NativeOverlayRuleDiagnostic
        {
            MarkerAci = map.MarkerAci,
            RuleId = map.RuleId,
            TargetLayer = map.Layer,
            BoundaryPriority = map.BoundaryPriority,
            RemapFills = map.RemapFills,
            Wrapping = map.RuleId.StartsWith("wrap:", StringComparison.Ordinal),
            PreserveNative = map.PreserveNative,
            ExpectedRevitMatches = request.ExpectedRuleMatches.GetValueOrDefault(map.RuleId),
            SourceLayers = new(map.SourceLayers),
            SourceCategories = new(map.DiagnosticSourceCategories),
            CompoundLayerIndices = new(map.DiagnosticCompoundLayerIndices),
            CompoundLayerFunctions = new(map.DiagnosticCompoundLayerFunctions),
            MaterialNames = new(map.DiagnosticMaterialNames),
            SourceElementCount = map.DiagnosticSourceElementCount,
            SourceElementIds = new(map.DiagnosticSourceElementIds)
        });
        response.NativeOverlayRuleDiagnostics = request.ColorRemaps
            .Select(map => diagnostics[map.MarkerAci]).ToList();
        var markerSignatures = diagnostics.Keys.ToDictionary(marker => marker,
            _ => new HashSet<string>(StringComparer.Ordinal));
        var markerLineSignatures = diagnostics.Keys.ToDictionary(marker => marker,
            _ => new HashSet<string>(StringComparer.Ordinal));
        static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) key = "(없음)";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        NativeOverlayRuleDiagnostic? Diagnostic(OverlayTarget target) => target.MarkerAci > 0
            && diagnostics.TryGetValue(target.MarkerAci, out NativeOverlayRuleDiagnostic? value) ? value : null;
        static string ShortGeometry(string value) => value.Length <= 240 ? value : value[..240] + "…";
        void Sample(OverlayTarget target, string result, string geometry, string nativeLayer = "",
            string nativeType = "", string detail = "")
        {
            NativeOverlayRuleDiagnostic? diagnostic = Diagnostic(target);
            if (diagnostic == null) return;
            if (diagnostic.Samples.Count >= 20) { diagnostic.OmittedSamples++; return; }
            diagnostic.Samples.Add(new NativeOverlayDiagnosticSample
            {
                Result = result,
                MarkerGeometry = ShortGeometry(geometry),
                NativeLayer = nativeLayer,
                NativeEntityType = nativeType,
                Detail = detail
            });
        }
        void Reject(OverlayTarget target, string reason, string geometry, string nativeLayer = "",
            string nativeType = "", string detail = "")
        {
            NativeOverlayRuleDiagnostic? diagnostic = Diagnostic(target);
            if (diagnostic == null) return;
            Increment(diagnostic.RejectionReasons, reason);
            Sample(target, reason, geometry, nativeLayer, nativeType, detail);
        }
        var marks = new Dictionary<string, List<OverlayTarget>>(StringComparer.Ordinal);
        var lineMarks = new Dictionary<string, List<OverlayLine>>(StringComparer.Ordinal);
        int classifiedOccurrences = 0;
        WalkWorld(classified.ModelSpace, (entity, transform, _) =>
        {
            check();
            if (!targets.TryGetValue(entity.Layer.Name, out OverlayTarget? target)) return;
            IReadOnlyList<OverlayWorldLine> worldSegments = WorldLines(entity, transform);
            if (Diagnostic(target) is { } diagnostic)
            {
                diagnostic.ClassifiedEntities++;
                Increment(diagnostic.MarkerEntityTypes, entity.ObjectName);
                diagnostic.ClassifiedLines += worldSegments.Count;
            }
            string? signature = NativeSignature(entity, transform);
            if (signature == null)
            {
                if (Diagnostic(target) is { } unsupported) unsupported.UnsupportedMarkerEntities++;
                Reject(target, "지원하지 않는 분류 형상", entity.ObjectName, nativeType: entity.ObjectName);
                return;
            }
            if (markerSignatures.TryGetValue(target.MarkerAci, out HashSet<string>? signatures)) signatures.Add(signature);
            if (entity is Line && markerLineSignatures.TryGetValue(target.MarkerAci, out HashSet<string>? lineSignatures))
                lineSignatures.Add(signature);
            if (!marks.TryGetValue(signature, out List<OverlayTarget>? values))
                marks[signature] = values = new();
            values.Add(target);
            foreach (OverlayWorldLine segment in worldSegments)
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
            {
                ambiguous += pair.Value.Count;
                foreach (OverlayTarget target in pair.Value)
                    Reject(target, "동일 형상 분류 충돌", pair.Key, detail: "같은 형상에 서로 다른 최우선 대상이 있습니다.");
                continue;
            }
        }

        var nativeSignatures = new HashSet<string>(StringComparer.Ordinal);
        var nativeHatches = new Dictionary<string, List<Hatch>>(StringComparer.Ordinal);
        var nativeHatchOccurrences = new List<(Hatch Entity, string Signature)>();
        var assignments = new Dictionary<Entity, List<OverlayTarget>>();
        var lineOccurrences = new Dictionary<Line, List<List<OverlayPiece>>>(ReferenceEqualityComparer.Instance);
        var lineOwners = new Dictionary<Line, BlockRecord>(ReferenceEqualityComparer.Instance);
        var polylineOccurrences = new Dictionary<LwPolyline, List<List<List<OverlayPiece>>>>(ReferenceEqualityComparer.Instance);
        IReadOnlyList<OverlayLine> CandidateLines(Entity entity, OverlayWorldLine nativeLine)
        {
            IReadOnlyList<OverlayLine> candidates = lineMarks.TryGetValue(nativeLine.Key, out List<OverlayLine>? lineValues)
                ? lineValues : Array.Empty<OverlayLine>();
            foreach (OverlayLine candidate in candidates.Where(candidate => candidate.End > nativeLine.Start + 1e-5
                && candidate.Start < nativeLine.End - 1e-5))
            {
                if (Diagnostic(candidate.Target) is not { } diagnostic) continue;
                diagnostic.CollinearNativeCandidates++;
                Increment(diagnostic.CandidateNativeLayers, entity.Layer.Name);
                if (AcceptsSourceLayer(candidate.Target, entity.Layer.Name)) diagnostic.AcceptedSourceCandidates++;
                else
                {
                    diagnostic.RejectedSourceCandidates++;
                    Increment(diagnostic.RejectedNativeLayers, entity.Layer.Name);
                    Reject(candidate.Target, "Native 원본 레이어 제한", nativeLine.Key, entity.Layer.Name,
                        entity.ObjectName, "형상은 겹치지만 SourceLayers에 없는 원본 레이어입니다.");
                }
            }
            return candidates;
        }
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
                IReadOnlyList<OverlayLine> candidates = CandidateLines(entity, nativeLine);
                if (candidates.Count == 0 && marks.TryGetValue(signature, out List<OverlayTarget>? exactLines))
                {
                    var exactCandidates = exactLines.Where(candidate => AcceptsSourceLayer(candidate, entity.Layer.Name)).ToList();
                    foreach (OverlayTarget candidate in exactLines)
                    {
                        if (Diagnostic(candidate) is not { } diagnostic) continue;
                        Increment(diagnostic.CandidateNativeLayers, entity.Layer.Name);
                        if (AcceptsSourceLayer(candidate, entity.Layer.Name)) diagnostic.AcceptedSourceCandidates++;
                        else
                        {
                            diagnostic.RejectedSourceCandidates++;
                            Increment(diagnostic.RejectedNativeLayers, entity.Layer.Name);
                            Reject(candidate, "Native 원본 레이어 제한", signature, entity.Layer.Name,
                                entity.ObjectName, "직선 끝점은 정확히 같지만 SourceLayers에 없는 원본 레이어입니다.");
                        }
                    }
                    if (exactCandidates.Count > 0)
                    {
                        candidates = exactCandidates.Select(candidate =>
                            new OverlayLine(nativeLine.Start, nativeLine.End, candidate)).ToList();
                        foreach (OverlayTarget candidate in exactCandidates)
                            Sample(candidate, "직선 정확 서명 대체", signature, entity.Layer.Name, entity.ObjectName,
                                "공선 키 대신 동일한 양 끝점 서명으로 전체 직선을 판정합니다.");
                    }
                }
                if (!lineOccurrences.TryGetValue(nativeEntity, out List<List<OverlayPiece>>? occurrences))
                    lineOccurrences[nativeEntity] = occurrences = new();
                occurrences.Add(CoveredLinePieces(nativeLine, candidates, entity.Layer.Name));
                lineOwners.TryAdd(nativeEntity, owner);
                return;
            }
            if (entity is LwPolyline nativePolyline)
            {
                IReadOnlyList<OverlayWorldLine> segments = WorldLines(entity, transform);
                if (segments.Count > 0)
                {
                    var occurrence = new List<List<OverlayPiece>>();
                    foreach (OverlayWorldLine segment in segments)
                        occurrence.Add(CoveredLinePieces(segment, CandidateLines(entity, segment), entity.Layer.Name));
                    if (!polylineOccurrences.TryGetValue(nativePolyline, out List<List<List<OverlayPiece>>>? occurrences))
                        polylineOccurrences[nativePolyline] = occurrences = new();
                    occurrences.Add(occurrence);
                    return;
                }
            }
            OverlayTarget? target;
            if (!marks.TryGetValue(signature, out List<OverlayTarget>? exact)) return;
            foreach (OverlayTarget candidate in exact)
            {
                if (Diagnostic(candidate) is not { } diagnostic) continue;
                Increment(diagnostic.CandidateNativeLayers, entity.Layer.Name);
                if (AcceptsSourceLayer(candidate, entity.Layer.Name)) diagnostic.AcceptedSourceCandidates++;
                else
                {
                    diagnostic.RejectedSourceCandidates++;
                    Increment(diagnostic.RejectedNativeLayers, entity.Layer.Name);
                    Reject(candidate, "Native 원본 레이어 제한", signature, entity.Layer.Name,
                        entity.ObjectName, "정확한 형상이지만 SourceLayers에 없는 원본 레이어입니다.");
                }
            }
            if ((target = ResolveTarget(exact.Where(candidate => AcceptsSourceLayer(candidate, entity.Layer.Name)))) == null) return;
            if (!assignments.TryGetValue(entity, out List<OverlayTarget>? values))
                assignments[entity] = values = new();
            values.Add(target);
        });

        foreach (var pair in markerSignatures)
        {
            NativeOverlayRuleDiagnostic diagnostic = diagnostics[pair.Key];
            diagnostic.UniqueMarkerSignatures = pair.Value.Count;
            diagnostic.ExactNativeSignatures = pair.Value.Count(nativeSignatures.Contains);
            diagnostic.MissingNativeSignatures = pair.Value.Count - diagnostic.ExactNativeSignatures;
            HashSet<string> lineSignatures = markerLineSignatures[pair.Key];
            diagnostic.UniqueMarkerLineSignatures = lineSignatures.Count;
            diagnostic.ExactNativeLineSignatures = lineSignatures.Count(nativeSignatures.Contains);
            if (diagnostic.ExactNativeLineSignatures > 0 && diagnostic.CollinearNativeCandidates == 0
                && diagnostic.AcceptedSourceCandidates == 0)
                diagnostic.RejectionReasons["직선 정확 형상은 있으나 공선 키 후보 없음"] = diagnostic.ExactNativeLineSignatures;
            if (diagnostic.MissingNativeSignatures == 0) continue;
            diagnostic.RejectionReasons["Native 정확 형상 없음"] = diagnostic.MissingNativeSignatures;
            OverlayTarget? target = targets.Values.FirstOrDefault(candidate => candidate.MarkerAci == pair.Key);
            if (target == null) continue;
            foreach (string signature in pair.Value.Where(signature => !nativeSignatures.Contains(signature)).Take(20))
                Sample(target, "Native 정확 형상 없음", signature,
                    detail: "동일 서명이 없습니다. 직선은 별도의 공선·부분구간 후보 통계를 함께 확인하세요.");
        }

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
                foreach (OverlayTarget target in occurrences.SelectMany(pieces => pieces)
                    .Where(piece => piece.Target != null).Select(piece => piece.Target!).Distinct())
                    Reject(target, "공유 블록 배치별 판정 불일치", PieceKey(occurrences[0]),
                        pair.Key.Layer.Name, pair.Key.ObjectName,
                        $"동일 블록 정의의 배치별 구간 판정이 {variants.Count:N0}가지입니다.");
                continue;
            }
            List<OverlayPiece> pieces = occurrences[0];
            if (!pieces.Any(piece => piece.Target is { PreserveNative: false }))
            {
                foreach (OverlayTarget owner in pieces.Where(piece => piece.Target?.PreserveNative == true)
                    .Select(piece => piece.Target!).Distinct())
                {
                    if (Diagnostic(owner) is { } diagnostic) diagnostic.PreservedEntities++;
                    Sample(owner, "Native 소유권 유지", PieceKey(pieces), pair.Key.Layer.Name, pair.Key.ObjectName);
                }
                continue;
            }
            if (pieces.Count == 1 && pieces[0].Target is { } whole)
            {
                if (whole.PreserveNative)
                {
                    if (Diagnostic(whole) is { } preserved) preserved.PreservedEntities++;
                    Sample(whole, "Native 소유권 유지", PieceKey(pieces), pair.Key.Layer.Name, pair.Key.ObjectName);
                    continue;
                }
                assignments[pair.Key] = new() { whole };
                if (Diagnostic(whole) is { } diagnostic) diagnostic.FullLineAssignments++;
                Sample(whole, "전체 직선 판정", NativeSignature(pair.Key, Transform.CreateTranslation(XYZ.Zero)) ?? "L",
                    pair.Key.Layer.Name, pair.Key.ObjectName);
                continue;
            }
            if (!lineOwners.ContainsKey(pair.Key))
            {
                sharedAmbiguous += occurrences.Count;
                foreach (OverlayTarget target in pieces.Where(piece => piece.Target != null).Select(piece => piece.Target!).Distinct())
                    Reject(target, "원본 직선 소유 블록 없음", PieceKey(pieces), pair.Key.Layer.Name, pair.Key.ObjectName);
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
                if (piece.Target != null)
                {
                    if (piece.Target.PreserveNative)
                    {
                        if (Diagnostic(piece.Target) is { } preserved) preserved.PreservedEntities++;
                        Sample(piece.Target, "Native 부분 소유권 유지", PieceKey(new[] { piece }), pair.Key.Layer.Name,
                            pair.Key.ObjectName, $"원본 직선 구간 {piece.Start:R}~{piece.End:R}");
                    }
                    else
                    {
                        assignments[clone] = new() { piece.Target };
                        if (Diagnostic(piece.Target) is { } diagnostic) diagnostic.PartialLineAssignments++;
                        Sample(piece.Target, "부분 직선 판정", PieceKey(new[] { piece }), pair.Key.Layer.Name,
                            pair.Key.ObjectName, $"원본 직선 구간 {piece.Start:R}~{piece.End:R}");
                    }
                }
            }
            lineReplacements[pair.Key] = replacements;
            partialLinesSplit++;
        }
        foreach (var pair in polylineOccurrences)
        {
            List<List<List<OverlayPiece>>> occurrences = pair.Value;
            var variants = occurrences.Select(segments => string.Join("||", segments.Select(PieceKey)))
                .Distinct(StringComparer.Ordinal).ToList();
            if (variants.Count != 1)
            {
                sharedAmbiguous += occurrences.Count;
                foreach (OverlayTarget target in occurrences.SelectMany(segments => segments).SelectMany(pieces => pieces)
                    .Where(piece => piece.Target != null).Select(piece => piece.Target!).Distinct())
                    Reject(target, "공유 L자 폴리라인 배치별 판정 불일치", variants[0], pair.Key.Layer.Name,
                        pair.Key.ObjectName, $"동일 블록 정의의 배치별 세그먼트 판정이 {variants.Count:N0}가지입니다.");
                continue;
            }
            List<List<OverlayPiece>> segments = occurrences[0];
            bool fullyOwned = segments.Count > 0 && segments.All(pieces => pieces.Count == 1
                && pieces[0].Start <= 1e-9 && pieces[0].End >= 1 - 1e-9 && pieces[0].Target != null);
            if (!fullyOwned)
            {
                foreach (OverlayTarget target in segments.SelectMany(pieces => pieces)
                    .Where(piece => piece.Target != null).Select(piece => piece.Target!).Distinct())
                    Reject(target, "L자 폴리라인 일부 세그먼트만 판정", string.Join("||", segments.Select(PieceKey)),
                        pair.Key.Layer.Name, pair.Key.ObjectName, "Native 폴리라인 전체의 소유 대상이 같을 때만 레이어를 전사합니다.");
                continue;
            }
            var owners = segments.Select(pieces => pieces[0].Target!).ToList();
            OverlayTarget? selected = ResolveTarget(owners);
            if (selected == null || owners.Any(owner => TargetKey(owner) != TargetKey(selected)))
            {
                sharedAmbiguous += occurrences.Count;
                foreach (OverlayTarget target in owners.Distinct())
                    Reject(target, "L자 폴리라인 세그먼트 대상 충돌", string.Join("||", segments.Select(PieceKey)),
                        pair.Key.Layer.Name, pair.Key.ObjectName);
                continue;
            }
            if (selected.PreserveNative)
            {
                if (Diagnostic(selected) is { } preserved) preserved.PreservedEntities++;
                Sample(selected, "Native L자 폴리라인 소유권 유지", string.Join("||", segments.Select(PieceKey)),
                    pair.Key.Layer.Name, pair.Key.ObjectName);
                continue;
            }
            assignments[pair.Key] = new() { selected };
            if (Diagnostic(selected) is { } diagnostic) diagnostic.FullLineAssignments += segments.Count;
            Sample(selected, "전체 L자 폴리라인 판정", string.Join("||", segments.Select(PieceKey)),
                pair.Key.Layer.Name, pair.Key.ObjectName, $"직선 세그먼트 {segments.Count:N0}개");
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

        var counts = request.ColorRemaps.Where(map => !map.PreserveNative).Select(map => map.RuleId)
            .Concat(request.MaterialAppearanceRemaps.Select(map => map.RuleId))
            .Distinct(StringComparer.Ordinal).ToDictionary(rule => rule, _ => 0, StringComparer.Ordinal);
        int applied = 0;
        foreach (var pair in assignments)
        {
            OverlayTarget? selected = ResolveTarget(pair.Value);
            if (selected == null)
            {
                sharedAmbiguous += pair.Value.Count;
                foreach (OverlayTarget candidate in pair.Value)
                    Reject(candidate, "최종 대상 충돌", pair.Key.ObjectName, pair.Key.Layer.Name,
                        pair.Key.ObjectName, "한 Native 객체에 서로 다른 최우선 대상이 남았습니다.");
                continue;
            }
            OverlayTarget target = selected;
            string nativeLayer = pair.Key.Layer.Name;
            if (target.PreserveNative)
            {
                if (Diagnostic(target) is { } preserved) preserved.PreservedEntities++;
                Sample(target, "Native 소유권 유지", pair.Key.ObjectName, nativeLayer, pair.Key.ObjectName);
                continue;
            }
            if (!native.Layers.TryGetValue(target.Layer, out Layer? layer))
            {
                layer = (Layer)pair.Key.Layer.Clone();
                layer.Name = target.Layer;
                native.Layers.Add(layer);
            }
            layer.Color = new ACadSharp.Color((short)target.Color);
            pair.Key.Layer = layer;
            if (pair.Key is not Hatch) pair.Key.Color = ACadSharp.Color.ByLayer;
            if (Diagnostic(target) is { } diagnostic)
            {
                diagnostic.AppliedEntities++;
                Increment(diagnostic.AppliedNativeLayers, nativeLayer);
            }
            Sample(target, "최종 적용", pair.Key.ObjectName, nativeLayer, pair.Key.ObjectName,
                $"'{nativeLayer}' → '{target.Layer}'");
            counts[target.RuleId] = counts.GetValueOrDefault(target.RuleId) + 1;
            applied++;
        }

        int unmatched = marks.Where(pair => !nativeSignatures.Contains(pair.Key))
            .Sum(pair => pair.Value.Count(target => !target.PreserveNative));
        response.NativeOverlayMatchedEntities = applied;
        response.NativeOverlayUnmatchedMarkers = unmatched;
        response.NativeOverlayAmbiguousMarkers = ambiguous + sharedAmbiguous;
        response.NativeOverlayPartialLinesSplit = partialLinesSplit;
        response.CustomRuleEntityCounts = counts;
        foreach (var group in counts)
            response.Warnings.Add($"Native 필터 레이어 · 규칙 {group.Key} · 원본 DWG 객체 {group.Value:N0}개 반영");
        foreach (NativeOverlayRuleDiagnostic diagnostic in response.NativeOverlayRuleDiagnostics
            .Where(diagnostic => diagnostic.ClassifiedEntities > 0 || diagnostic.ExpectedRevitMatches > 0))
        {
            if (!diagnostic.PreserveNative && diagnostic.AcceptedSourceCandidates > 0
                && diagnostic.FullLineAssignments + diagnostic.PartialLineAssignments == 0
                && diagnostic.AppliedEntities == 0)
                diagnostic.RejectionReasons["허용 후보 후 구간 소유자 미결정"] = diagnostic.AcceptedSourceCandidates;
            if (!diagnostic.PreserveNative && diagnostic.FullLineAssignments + diagnostic.PartialLineAssignments > 0
                && diagnostic.AppliedEntities == 0)
                diagnostic.RejectionReasons["구간 판정 후 최종 대상 미적용"] =
                    diagnostic.FullLineAssignments + diagnostic.PartialLineAssignments;
            string origins = diagnostic.AppliedNativeLayers.Count == 0 ? "없음" : string.Join(", ",
                diagnostic.AppliedNativeLayers.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key} {pair.Value:N0}"));
            string kind = diagnostic.PreserveNative ? "Native 소유권 상세" : "Native 판정 상세";
            response.Warnings.Add($"{kind} · ACI {diagnostic.MarkerAci} · 규칙 {diagnostic.RuleId} · 대상 '{diagnostic.TargetLayer}'"
                + $" · 분류 {diagnostic.ClassifiedEntities:N0}(직선 {diagnostic.ClassifiedLines:N0})"
                + $" · 고유서명 {diagnostic.UniqueMarkerSignatures:N0} / 정확 {diagnostic.ExactNativeSignatures:N0} / 없음 {diagnostic.MissingNativeSignatures:N0}"
                + $" · 직선서명 {diagnostic.UniqueMarkerLineSignatures:N0} / 정확 {diagnostic.ExactNativeLineSignatures:N0}"
                + $" · 공선후보 {diagnostic.CollinearNativeCandidates:N0} / 허용 {diagnostic.AcceptedSourceCandidates:N0} / 원본레이어 거절 {diagnostic.RejectedSourceCandidates:N0}"
                + $" · 전체선 {diagnostic.FullLineAssignments:N0} / 부분선 {diagnostic.PartialLineAssignments:N0} / 최종 {diagnostic.AppliedEntities:N0} / Native 유지 {diagnostic.PreservedEntities:N0}"
                + $" · 최종 원본레이어 [{origins}]");
        }
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
        // Several Parts can resolve to the same output rule. Keep that output's
        // greatest real compound-layer rank instead of whichever marker happened
        // to be enumerated first.
        var candidates = source.GroupBy(TargetKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(TargetRank).ThenBy(candidate => candidate.MarkerAci).First()).ToList();
        if (candidates.Count == 0) return null;
        int bestRank = candidates.Max(TargetRank);
        var best = candidates.Where(candidate => TargetRank(candidate) == bestRank).ToList();
        // A same-rank unfiltered owner means the geometry cannot be attributed to
        // one filtered source object safely. Preserve the Native entity rather
        // than transferring another object's coincident marker.
        OverlayTarget? preserve = best.Where(candidate => candidate.PreserveNative)
            .OrderBy(candidate => candidate.MarkerAci).FirstOrDefault();
        if (preserve != null) return preserve;
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
            pieces = pieces.Select(piece => piece.Target is { RemapFills: false, Wrapping: false, PreserveNative: false }
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

    private static IReadOnlyList<OverlayWorldLine> WorldLines(Entity source, Transform transform)
    {
        if (source is Line)
            return WorldLine(source, transform) is { } line ? new[] { line } : Array.Empty<OverlayWorldLine>();
        if (source is not LwPolyline) return Array.Empty<OverlayWorldLine>();
        Entity clone = (Entity)source.Clone();
        try { TransformGeometry(clone, transform); }
        catch { return Array.Empty<OverlayWorldLine>(); }
        if (clone is not LwPolyline polyline || polyline.Vertices.Count < 2) return Array.Empty<OverlayWorldLine>();
        var vertices = polyline.Vertices.ToList();
        int count = polyline.IsClosed ? vertices.Count : vertices.Count - 1;
        var result = new List<OverlayWorldLine>();
        Transform identity = Transform.CreateTranslation(XYZ.Zero);
        for (int index = 0; index < count; index++)
        {
            LwPolyline.Vertex start = vertices[index], end = vertices[(index + 1) % vertices.Count];
            if (Math.Abs(start.Bulge) > Epsilon) continue;
            var segment = new Line
            {
                StartPoint = new XYZ(start.Location.X, start.Location.Y, polyline.Elevation),
                EndPoint = new XYZ(end.Location.X, end.Location.Y, polyline.Elevation)
            };
            if (WorldLine(segment, identity) is { } world) result.Add(world);
        }
        return result;
    }

    private static OverlayWorldLine? WorldLine(Entity source, Transform transform)
    {
        if (source is not Line) return null;
        Entity clone = (Entity)source.Clone();
        try { TransformGeometry(clone, transform); }
        catch { return null; }
        if (clone is not Line line) return null;
        static double R(double value)
        {
            double rounded = Math.Round(value, 6);
            return rounded == 0 ? 0 : rounded;
        }
        XYZ startPoint = new(R(line.StartPoint.X), R(line.StartPoint.Y), R(line.StartPoint.Z));
        XYZ endPoint = new(R(line.EndPoint.X), R(line.EndPoint.Y), R(line.EndPoint.Z));
        double dx = endPoint.X - startPoint.X, dy = endPoint.Y - startPoint.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < Epsilon || Math.Abs(endPoint.Z - startPoint.Z) > 1e-5) return null;
        double x = dx / length, y = dy / length;
        if (x < -Epsilon || (Math.Abs(x) < Epsilon && y < 0)) { x = -x; y = -y; }
        double offset = -y * startPoint.X + x * startPoint.Y;
        double first = x * startPoint.X + y * startPoint.Y;
        double second = x * endPoint.X + y * endPoint.Y;
        static string N(double value) => Math.Round(value, 5).ToString("R", CultureInfo.InvariantCulture);
        return new OverlayWorldLine(N(x) + "|" + N(y) + "|" + N(offset) + "|" + N(startPoint.Z),
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
        int Priority, bool Wrapping, bool RemapFills, List<string> SourceLayers, int MarkerAci, bool PreserveNative);
    private sealed record OverlayLine(double Start, double End, OverlayTarget Target);
    private sealed record OverlayWorldLine(string Key, double Start, double End, bool Forward);
    private sealed record OverlayPiece(double Start, double End, OverlayTarget? Target);
}
