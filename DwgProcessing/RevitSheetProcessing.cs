using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ChangExport.DwgProcessing;

public sealed partial class ManagedDwgProcessor
{
    private static bool IsRevitMetadataWarning(string message) =>
        message.StartsWith("Entry not found ", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.IsMatch(message,
            @"^Entry not found (CLASS_REGISTRY|DBUNITS|DWG_REGCOUNT|DWGID|ELEMCOUNT)\|[0-9A-F]+ for dictionary DSCA\|[0-9A-F]+$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void PrepareRevitSheet(CadDocument source, BridgeResponse response)
    {
        foreach (Layout layout in source.Layouts.Where(l => l.IsPaperSpace))
        {
            layout.PaperUnits = PlotPaperUnits.Millimeters;
            var views = layout.AssociatedBlock.Entities.OfType<Viewport>().ToList();
            // ACadSharp's RepresentsPaper is calculated from collection order, not a DWG ID.
            // Revit writes drawing viewports BEFORE the default layout viewport. Recognize
            // the full Revit default signature, independent of its collection position.
            var paper = views.Where(v => Math.Abs(v.Width - 12) < Epsilon && Math.Abs(v.Height - 9) < Epsilon
                && v.Center.DistanceFrom(new XYZ(6, 4.5, 0)) < Epsilon
                && v.ViewCenter.DistanceFrom(new XY(6, 4.5)) < Epsilon && Math.Abs(v.ViewHeight - 12) < Epsilon
                && v.ViewTarget.DistanceFrom(XYZ.Zero) < Epsilon && Math.Abs(v.TwistAngle) < Epsilon
                && v.Status == (ViewportStatusFlags.CurrentlyAlwaysEnabled | ViewportStatusFlags.UcsIconVisibility)).ToList();
            if (paper.Count != 1 && views.Count > 0)
                throw new InvalidDataException("Revit 배치 기본 뷰포트를 식별할 수 없습니다. 실제 뷰를 임의로 생략하지 않았습니다.");
            if (paper.Count == 0) continue;
            // The Revit path tests ActiveStatus, never the SDK's computed RepresentsPaper.
            paper[0].ActiveStatus = 0;
            response.ConvertedViewports += views.Count(v => !ReferenceEquals(v, paper[0]) && IsEnabledViewport(v) && !OmitViewport(v));
        }
    }

    private static bool IsEnabledViewport(Viewport v) => v.ActiveStatus != 0 && !v.Status.HasFlag(ViewportStatusFlags.ViewportOff);

    private static int BindReferences(CadDocument source, string path, List<string> warnings, Action check, HashSet<string> chain,
        Dictionary<string, string> layerNames, IReadOnlyList<ColorLayerRemap> remaps, Action<CadDocument>? prepare = null)
    {
        string fullPath = Path.GetFullPath(path);
        if (chain.Count >= 32 || !chain.Add(fullPath)) throw new InvalidDataException("외부 참조 순환 또는 깊이 초과: " + fullPath);
        int ignoredContainerMarkers = 0;
        var markerRgb = remaps.Select(map => ColorRgb(new ACadSharp.Color((short)map.MarkerAci))).ToHashSet();
        try
        {
            int index = 0;
            foreach (BlockRecord block in source.BlockRecords.Where(b => (b.Flags & (BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay)) != 0).ToArray())
            {
                check();
                string raw = block.BlockEntity.XRefPath;
                string folder = Path.GetDirectoryName(fullPath)!;
                // Resolve only files generated beside this drawing, never arbitrary stored
                // absolute paths, network shares or a CAD application's search path.
                string candidate = Path.Combine(folder, Path.GetFileName(raw));
                if (string.IsNullOrWhiteSpace(raw) || !File.Exists(candidate))
                    throw new FileNotFoundException("외부 참조 DWG를 찾을 수 없습니다: " + raw, candidate);
                CadDocument reference = Read(candidate, warnings);
                prepare?.Invoke(reference);
                var nestedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ignoredContainerMarkers += BindReferences(reference, candidate, warnings, check, chain, nestedNames, remaps, prepare);
                foreach (Insert container in source.BlockRecords.SelectMany(record => record.Entities).OfType<Insert>()
                    .Where(insert => ReferenceEquals(insert.Block, block)))
                {
                    // Revit can put one Part override marker on the placed-view XREF
                    // container. It identifies no Revit element and must never be
                    // inherited by every line, dimension and annotation in the view.
                    if (container.Color.IsByLayer || container.Color.IsByBlock || !markerRgb.Contains(ColorRgb(container.Color))) continue;
                    container.Color = ACadSharp.Color.ByLayer;
                    ignoredContainerMarkers++;
                }
                string prefix = "CE_X" + ++index + "_";
                // Honor namespaced host layers, including per-viewport frozen layers.
                var names = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
                foreach (Layer layer in reference.Layers)
                {
                    string hostName = block.Name + "|" + layer.Name;
                    if (!source.Layers.TryGetValue(hostName, out Layer host))
                    {
                        host = (Layer)layer.Clone(); host.Name = hostName; source.Layers.Add(host);
                    }
                    names[layer.Name] = host;
                    layerNames[hostName] = nestedNames.TryGetValue(layer.Name, out string? original) ? original : layer.Name;
                }
                void Relayer(Entity entity, HashSet<BlockRecord> visited)
                {
                    if (names.TryGetValue(entity.Layer.Name, out Layer? layer)) entity.Layer = layer;
                    if (entity is Insert insert) foreach (var a in insert.Attributes) Relayer(a, visited);
                    BlockRecord? child = entity is Insert i ? i.Block : entity is Dimension d ? d.Block : null;
                    if (child != null && visited.Add(child)) foreach (Entity e in child.Entities) Relayer(e, visited);
                }
                if (block.Entities.Count != 0) throw new InvalidDataException("외부 참조 블록에 예상하지 못한 로컬 객체가 있습니다: " + block.Name);
                var ordered = new List<Entity>();
                foreach (Entity entity in reference.ModelSpace.GetSortedEntities())
                {
                    Entity clone = (Entity)entity.Clone();
                    Relayer(clone, new HashSet<BlockRecord>());
                    // Only isolate names here. Validation/allowed omissions happen after
                    // viewport selection, so shaded geometry cannot stop another 2D view.
                    RenameReferenceBlocks(clone, prefix, new HashSet<BlockRecord>());
                    block.Entities.Add(clone);
                    ordered.Add(clone);
                }
                PreserveMaskDrawOrder(block, ordered);
                block.BlockEntity.BasePoint = reference.Header.ModelSpaceInsertionBase;
                block.Flags &= ~(BlockTypeFlags.XRef | BlockTypeFlags.XRefOverlay | BlockTypeFlags.XRefDependent | BlockTypeFlags.XRefResolved | BlockTypeFlags.Referenced);
                block.BlockEntity.XRefPath = "";
                warnings.Add($"결합: {Path.GetFileName(candidate)} · 모형공간 객체 {reference.ModelSpace.Entities.Count:N0}개");
            }
        }
        finally { chain.Remove(fullPath); }
        return ignoredContainerMarkers;
    }

    private static void RestoreReferenceLayerNames(CadDocument document, Dictionary<string, string> names, List<string> warnings)
    {
        var replacements = new Dictionary<Layer, Layer>();
        foreach (var pair in names)
        {
            if (!document.Layers.TryGetValue(pair.Key, out Layer layer)) continue;
            layer.Flags &= ~(LayerFlags.XrefDependent | LayerFlags.XrefResolved | LayerFlags.Referenced);
            if (!document.Layers.TryGetValue(pair.Value, out Layer original)) { layer.Name = pair.Value; continue; }
            var meaningful = ~(LayerFlags.XrefDependent | LayerFlags.XrefResolved | LayerFlags.Referenced);
            if (layer.Color.Equals(original.Color) && layer.LineType.Name == original.LineType.Name && layer.LineWeight == original.LineWeight
                && layer.IsOn == original.IsOn && layer.PlotFlag == original.PlotFlag && (layer.Flags & meaningful) == (original.Flags & meaningful))
                replacements[layer] = original;
            else warnings.Add($"레이어 표시 보존: '{pair.Value}'의 참조별 속성이 달라 '{pair.Key}' 이름을 유지했습니다.");
        }
        foreach (BlockRecord block in document.BlockRecords)
        foreach (Entity entity in block.Entities)
        {
            if (replacements.TryGetValue(entity.Layer, out Layer? layer)) entity.Layer = layer;
            if (entity is Insert i) foreach (var a in i.Attributes) if (replacements.TryGetValue(a.Layer, out layer)) a.Layer = layer;
        }
        foreach (Layer old in replacements.Keys) document.Layers.Remove(old.Name);
    }

    private static void RenameReferenceBlocks(Entity entity, string prefix, HashSet<BlockRecord> seen)
    {
        if (entity is MText m) m.Style.Name = prefix + m.Style.Name;
        if (entity is TextEntity t) t.Style.Name = prefix + t.Style.Name;
        if (entity is Dimension dim) { dim.Style.Name = prefix + dim.Style.Name; dim.Style.Style.Name = prefix + dim.Style.Style.Name; }
        if (entity is Insert i) foreach (var a in i.Attributes) RenameReferenceBlocks(a, prefix, seen);
        BlockRecord? block = entity is Insert insert ? insert.Block : entity is Dimension d ? d.Block : null;
        if (block == null || !seen.Add(block)) return;
        block.Name = prefix + block.Name.TrimStart('*'); block.Flags &= ~BlockTypeFlags.Anonymous;
        foreach (Entity child in block.Entities) RenameReferenceBlocks(child, prefix, seen);
    }

    private static void IsolateConflictingStyles(CadDocument source, CadDocument target, int sheet, List<string> warnings)
    {
        foreach (LineType type in source.LineTypes.ToArray())
        {
            if (!target.LineTypes.TryGetValue(type.Name, out LineType other)
                || type.Segments.Select(SegmentSignature).SequenceEqual(other.Segments.Select(SegmentSignature))) continue;
            string original = type.Name, name = $"CE_S{sheet}_{original}";
            while (source.LineTypes.Contains(name) || target.LineTypes.Contains(name)) name = "_" + name;
            type.Name = name;
            warnings.Add($"선종류 보존: 시트 {sheet}의 '{original}' 정의가 달라 '{name}'으로 분리했습니다.");
        }
        foreach (Layer layer in source.Layers.Where(l => l.Name != "0").ToArray())
        {
            if (!target.Layers.TryGetValue(layer.Name, out Layer other)
                || (layer.Color.Equals(other.Color) && layer.LineWeight == other.LineWeight && layer.LineType.Name == other.LineType.Name
                    && layer.Flags == other.Flags && layer.IsOn == other.IsOn && layer.PlotFlag == other.PlotFlag)) continue;
            string original = layer.Name, name = $"CE_S{sheet}_{original}";
            while (source.Layers.Contains(name) || target.Layers.Contains(name)) name = "_" + name;
            layer.Name = name;
            warnings.Add($"레이어 표시 보존: 시트 {sheet}의 '{original}' 속성이 달라 '{name}'으로 분리했습니다.");
        }
    }

    public static HashSet<int> UsedColorIndices(IEnumerable<string> paths)
    {
        var used = new HashSet<int>();
        foreach (string path in paths)
        {
            CadDocument doc = Read(path, new List<string>());
            var entities = doc.BlockRecords.SelectMany(b => b.Entities).ToList();
            foreach (var color in doc.Layers.Select(l => l.Color).Concat(entities.Select(e => e.Color))
                .Concat(entities.OfType<Insert>().SelectMany(i => i.Attributes).Select(a => a.Color)))
                if (!color.IsByBlock && !color.IsByLayer) used.Add(ColorRgb(color));
        }
        return Enumerable.Range(1, 255).Where(i => used.Contains(ColorRgb(new ACadSharp.Color((short)i)))).ToHashSet();
    }

    private static int ColorRgb(ACadSharp.Color color) => (color.R << 16) | (color.G << 8) | color.B;

    private static CadDocument ApplyCustomRemaps(CadDocument document, BridgeRequest request, BridgeResponse response, GeometryContext? geometry = null)
    {
        if (request.ColorRemaps.Count == 0 && request.TextReplacements.Count == 0)
        {
            foreach (var entity in document.Entities) CaptureWidths(entity, geometry);
            return document;
        }
        var rewritten = CreateOutput(document);
        int blockIndex = 0;
        var mappings = request.ColorRemaps.ToDictionary(m => m.MarkerAci);
        var counts = mappings.Keys.ToDictionary(k => k, _ => 0);
        var materialBoundaries = new Dictionary<Entity, int>();
        var byRgb = request.ColorRemaps.ToDictionary(m => ColorRgb(new ACadSharp.Color((short)m.MarkerAci)));
        ColorLayerRemap? Marker(ACadSharp.Color color) => !color.IsByBlock && !color.IsByLayer
            && byRgb.TryGetValue(ColorRgb(color), out var map) ? map : null;
        string Replace(string value)
        {
            foreach (var entry in request.TextReplacements) value = value.Replace(entry.Key, entry.Value, StringComparison.Ordinal);
            return value;
        }
        static bool IsLowerGraphic(Entity entity)
        {
            static bool Match(string value)
            {
                string normalized = value.Trim().Trim('<', '>').Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
                return normalized is "BEYOND" or "UNDERLAY" or "하부" or "아래" or "하부표현" or "하부선";
            }
            return Match(entity.Layer.Name) || Match(entity.LineType.Name) || Match(entity.Layer.LineType.Name)
                || entity.Layer.Name.Split('|').Any(Match) || entity.LineType.Name.Split('|').Any(Match)
                || entity.Layer.LineType.Name.Split('|').Any(Match);
        }
        static string? BoundarySignature(Entity entity)
        {
            static string Number(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            static string Point(XYZ point) => $"{Number(point.X)},{Number(point.Y)},{Number(point.Z)}";
            if (entity is Line line)
            {
                string start = Point(line.StartPoint), end = Point(line.EndPoint);
                return string.CompareOrdinal(start, end) <= 0 ? $"L|{start}|{end}" : $"L|{end}|{start}";
            }
            if (entity is Circle circle)
                return $"C|{Point(circle.Center)}|{Number(circle.Radius)}|{Point(circle.Normal)}";
            if (entity is Arc arc)
            {
                double start = arc.StartAngle % (2 * Math.PI), end = arc.EndAngle % (2 * Math.PI);
                if (start < 0) start += 2 * Math.PI;
                if (end < 0) end += 2 * Math.PI;
                return $"A|{Point(arc.Center)}|{Number(arc.Radius)}|{Number(start)}|{Number(end)}|{Point(arc.Normal)}";
            }
            if (entity is LwPolyline polyline)
            {
                string forward = string.Join(";", polyline.Vertices.Select(v => $"{Number(v.Location.X)},{Number(v.Location.Y)},{Number(v.Bulge)}"));
                string reverse = string.Join(";", polyline.Vertices.AsEnumerable().Reverse().Select(v => $"{Number(v.Location.X)},{Number(v.Location.Y)},{Number(-v.Bulge)}"));
                string points = string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse;
                return $"P|{polyline.IsClosed}|{Number(polyline.Elevation)}|{points}";
            }
            return null;
        }
        void RemoveDuplicateMaterialBoundaries(BlockRecord block)
        {
            var groups = block.Entities.Where(materialBoundaries.ContainsKey)
                .Select(entity => (Entity: entity, Signature: BoundarySignature(entity)))
                .Where(item => item.Signature != null)
                .GroupBy(item => item.Signature!, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var ordered = group.OrderByDescending(item => materialBoundaries[item.Entity]).ToList();
                foreach (var duplicate in ordered.Skip(1))
                {
                    block.Entities.Remove(duplicate.Entity);
                    response.MaterialBoundaryDuplicatesRemoved++;
                }
            }
        }
        void Visit(Entity e, ColorLayerRemap? inherited, HashSet<BlockRecord> visited)
        {
            bool isFill = IsRevitFillDisplay(e), isMask = IsRevitMask(e);
            var candidate = Marker(e.Color.IsByLayer ? e.Layer.Color : e.Color) ?? inherited;
            bool lower = candidate != null && !isFill && !isMask && IsLowerGraphic(e);
            if (lower) response.FilterLowerGraphicsSkipped++;
            // Masking never moves. Material fills move to the exact material layer,
            // but keep their explicit Revit color and hatch definition.
            var map = lower || isMask || (isFill && candidate?.RemapFills != true) ? null : candidate;
            if (map != null)
            {
                if (!rewritten.Layers.TryGetValue(map.Layer, out Layer layer))
                { layer = (Layer)e.Layer.Clone(); layer.Name = map.Layer; layer.Color = new ACadSharp.Color((short)map.Color); rewritten.Layers.Add(layer); }
                layer.Color = new ACadSharp.Color((short)map.Color);
                e.Layer = layer;
                if (!isFill) e.Color = ACadSharp.Color.ByLayer;
                if (map.BoundaryPriority > 0 && !isFill) materialBoundaries[e] = map.BoundaryPriority;
                counts[map.MarkerAci]++;
            }
            if (e is MText m) m.Value = Replace(m.Value);
            if (e is TextEntity t) t.Value = Replace(t.Value);
            if (e is Insert i) foreach (var a in i.Attributes) Visit(a, map, visited);
            BlockRecord? block = e is Insert insert ? insert.Block : e is Dimension d ? d.Block : null;
            if (block != null && visited.Add(block))
            {
                // Work on detached deep clones. Two differently filtered parent types can
                // share a nested family block; never recolor their shared definition in place.
                block.Name = "CE_FILTER_" + ++blockIndex + "_" + block.Name.TrimStart('*');
                foreach (Entity child in block.Entities) Visit(child, map, visited);
                RemoveDuplicateMaterialBoundaries(block);
            }
        }
        foreach (Entity source in document.ModelSpace.GetSortedEntities())
        {
            var clone = (Entity)source.Clone(); CaptureWidths(clone, geometry); Visit(clone, null, new HashSet<BlockRecord>()); rewritten.Entities.Add(clone);
        }
        RemoveDuplicateMaterialBoundaries(rewritten.ModelSpace);
        PreserveMaskDrawOrder(rewritten.ModelSpace, rewritten.ModelSpace.GetSortedEntities().ToArray());
        if (response.FilterLowerGraphicsSkipped > 0)
            response.Warnings.Add($"하부 표현 보호: Beyond/Underlay 선 {response.FilterLowerGraphicsSkipped:N0}개는 유형·재료 필터를 적용하지 않았습니다.");
        if (response.MaterialBoundaryDuplicatesRemoved > 0)
            response.Warnings.Add($"복합재료 공유 경계: 기능 우선순위에 따라 중복선 {response.MaterialBoundaryDuplicatesRemoved:N0}개를 정리했습니다.");
        foreach (var map in request.ColorRemaps)
            response.Warnings.Add($"필터 레이어 '{map.Layer}' / ACI {map.Color} · DWG 객체 {counts[map.MarkerAci]:N0}개 반영");
        response.CustomRuleEntityCounts = request.ColorRemaps.GroupBy(m => m.RuleId).ToDictionary(g => g.Key, g => g.Sum(m => counts[m.MarkerAci]));
        foreach (var expected in request.ExpectedRuleMatches.Where(p => p.Value > 0))
            if (!response.CustomRuleEntityCounts.TryGetValue(expected.Key, out int count) || count == 0)
                response.Warnings.Add($"필터 미반영 확인 필요: 규칙 {expected.Key}는 Revit 객체 {expected.Value}개와 일치했지만 DWG 식별색 객체가 없습니다. 가림·생략 뷰·출력 색상 설정을 확인하세요.");
        SetExtents(rewritten);
        return rewritten;
    }
}
