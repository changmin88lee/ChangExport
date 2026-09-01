using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Color = ACadSharp.Color;

namespace ChangExport.DwgProcessing;

public sealed partial class ManagedDwgProcessor
{
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void TagFamilyBlocks(CadDocument document, GeometryContext context)
    {
        if (context.Request.FamilySources.Count == 0) return;
        foreach (var block in document.BlockRecords.ToArray())
        {
            if ((block.Flags & (ACadSharp.Blocks.BlockTypeFlags.XRef | ACadSharp.Blocks.BlockTypeFlags.XRefOverlay)) != 0) continue;
            var suffixes = System.Text.RegularExpressions.Regex.Matches(block.Name, @"-(?:[0-9]+|V[0-9]+)-").Cast<System.Text.RegularExpressions.Match>().ToArray();
            var matches = suffixes
                .SelectMany(m => context.FamilyIndex.GetValueOrDefault(FamilyKey(block.Name[..(m.Index + m.Length)])) ?? new())
                .DistinctBy(s => s.Identity).ToArray();
            string method = "이름·ID 일치";
            if (matches.Length == 0)
            {
                matches = suffixes.Where(m => m.Value.Length > 2 && char.IsDigit(m.Value[1]))
                    .SelectMany(m => context.DetailGroupIds.GetValueOrDefault(m.Value.Trim('-')) ?? new())
                    .DistinctBy(s => s.Identity).ToArray();
                method = "상세 그룹 ID 일치";
            }
            if (matches.Length == 0)
            {
                // Revit can use a geometry/linked-document ID or V1/V2 variant suffix.
                // Match the entire collected family/type name and reject ambiguities.
                matches = suffixes.SelectMany(m => context.FamilyNames.GetValueOrDefault(FamilyNameKey(block.Name[..m.Index])) ?? new())
                    .DistinctBy(s => s.Identity).ToArray();
                method = "전체 패밀리·유형 이름 일치";
            }
            if (matches.Length != 1)
            {
                if (matches.Length > 1 || (suffixes.Length > 0 && block.Name.Contains(" - ", StringComparison.Ordinal)))
                    context.FamilyMatches.Add(new(block.Name, "", matches.Length > 1 ? "동일 이름 후보 중복 · 개별 객체 유지" : "Revit 패밀리 미연결 · 개별 객체 유지"));
                continue;
            }
            var source = matches[0];
            context.FamilyMatches.Add(new(block.Name, source.Label, source.ExclusionReason.Length > 0 ? "제외: " + source.ExclusionReason : method));
            if (source.ExclusionReason.Length > 0) continue;
            string token = "CE_SRCF_" + Hash(source.Identity)[..24] + "_";
            context.Families[token] = new FamilyBlockInfo { Identity = source.Identity, Label = source.Label,
                IsTitleBlock = source.IsTitleBlock, IsDetailGroup = source.IsDetailGroup };
            block.Name = token + Guid.NewGuid().ToString("N");
        }
    }

    private static string FamilyNameKey(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

    private static string FamilyKey(string prefix)
    {
        // Revit replaces punctuation in native DWG names. Still require an exact known
        // ID and the whole normalized family/type prefix; never classify by a substring.
        int end = prefix.Length - 1, start = prefix.LastIndexOf('-', end - 1);
        if (start < 0) return prefix;
        string id = prefix[start..];
        return FamilyNameKey(prefix[..start]) + id;
    }

    private static FamilyBlockInfo? FamilyInfo(string name, GeometryContext? context) => context?.Families
        .FirstOrDefault(p => name.Contains(p.Key, StringComparison.Ordinal)).Value;

    private static void GroupFamily(List<Entity> output, int start, Transform transform, FamilyBlockInfo info,
        BridgeResponse response, GeometryContext geometry)
    {
        var members = output.Skip(start).ToArray();
        void Keep(string reason) => response.FamilyBlockFallbacks[reason] = response.FamilyBlockFallbacks.GetValueOrDefault(reason) + 1;
        if (members.Length == 0) return;
        if (!info.IsDetailGroup && (members.Any(e => e is Insert or Dimension or AttributeEntity or AttributeDefinition)
            || (!info.IsTitleBlock && members.Any(e => e is TextEntity or MText))))
        { Keep("주석·잘림 객체가 섞인 패밀리의 표시 순서 보존"); return; }
        if (!IsPlanarFamilyTransform(transform) || !Matrix4.Inverse(transform.Matrix, out var inverse))
        { Keep("반전·비균등·기울어진 패밀리의 형상 보존"); return; }
        var local = new List<Entity>();
        int dimension = 0;
        foreach (var member in members)
        {
            var clone = (Entity)member.Clone();
            TransformEditable(clone, new Transform(inverse), ref dimension);
            bool preserveWideColor = geometry.WideColorEntities.Contains(member);
            if (geometry.Request.UseLayerColors && !IsRevitFillDisplay(clone) && !IsRevitMask(clone) && !preserveWideColor)
            { clone.Color = Color.ByLayer; clone.BookColor = null; }
            if (preserveWideColor) geometry.WideColorEntities.Add(clone);
            local.Add(clone);
        }
        string label = new(info.Label.Select(c => "<>/\\\":;?*|=,".Contains(c) ? '_' : c).ToArray());
        if (label.Length > 80) label = label[..80];
        string token = label + "_CE_FAMILY_" + Guid.NewGuid().ToString("N");
        var block = new BlockRecord(token);
        foreach (var entity in local) block.Entities.Add(entity);
        var insert = PlaceFamily(block, transform);
        output.RemoveRange(start, output.Count - start); output.Add(insert);
        response.FamilyBlocks[token] = new FamilyBlockInfo { Identity = info.Identity, Label = info.Label,
            IsTitleBlock = info.IsTitleBlock, IsDetailGroup = info.IsDetailGroup, Processed = true };
    }

    private static bool IsPlanarFamilyTransform(Transform transform)
    {
        var x = Vector(transform, XYZ.AxisX); var y = Vector(transform, XYZ.AxisY);
        return Math.Abs(x.Z) < Epsilon && Math.Abs(y.Z) < Epsilon && x.GetLength() > Epsilon
            && Math.Abs(x.GetLength() - y.GetLength()) < Epsilon && XYZ.Cross(x, y).Z > 0;
    }

    private static Insert PlaceFamily(BlockRecord block, Transform transform)
    {
        var x = Vector(transform, XYZ.AxisX);
        double scale = x.GetLength();
        return new Insert(block) { InsertPoint = transform.ApplyTransform(XYZ.Zero), Normal = XYZ.AxisZ,
            Rotation = Math.Atan2(x.Y, x.X), XScale = scale, YScale = scale, ZScale = scale };
    }

    private static void DeduplicateFamilies(CadDocument document, BridgeResponse response, GeometryContext? context)
    {
        var clock = Stopwatch.StartNew();
        var known = new Dictionary<string, FamilyBlockInfo>(response.FamilyBlocks);
        if (context != null) foreach (var pair in context.Families.Where(p => p.Value.Processed)) known.TryAdd(pair.Key, pair.Value);
        var definitions = new Dictionary<string, List<(BlockRecord Block, string? Signature, bool Computed)>>();
        var surviving = new Dictionary<string, FamilyBlockInfo>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ordered = document.ModelSpace.GetSortedEntities().ToArray();
        var candidates = new Dictionary<Insert, KeyValuePair<string, FamilyBlockInfo>>(ReferenceEqualityComparer.Instance);
        foreach (var insert in ordered.OfType<Insert>())
        {
            var pair = known.FirstOrDefault(p => insert.Block.Name.Contains(p.Key, StringComparison.Ordinal));
            if (pair.Value != null) candidates[insert] = pair;
        }
        var signatures = new Dictionary<BlockRecord, string?>(ReferenceEqualityComparer.Instance);
        var rebuilt = new List<Entity>();
        bool changed = false;
        for (int index = 0; index < ordered.Length; index++)
        {
            if (ordered[index] is not Insert insert) { rebuilt.Add(ordered[index]); continue; }
            if (!candidates.TryGetValue(insert, out var pair)) { rebuilt.Add(insert); continue; }
            response.FamilyBlockReferences++;
            string bucketKey = pair.Value.Identity + ":" + FamilyPrefilterSignature(insert.Block);
            if (!definitions.TryGetValue(bucketKey, out var bucket))
            {
                definitions[bucketKey] = new() { (insert.Block, null, false) };
                surviving[insert.Block.Name] = pair.Value;
            }
            else
            {
                string? signature;
                if (signatures.TryGetValue(insert.Block, out var cached))
                {
                    signature = cached;
                    response.FamilySignatureCacheHits++;
                }
                else
                {
                    signature = FamilySignature(insert.Block);
                    signatures[insert.Block] = signature;
                    response.FamilySignaturesComputed++;
                }
                BlockRecord? same = null;
                if (signature != null)
                for (int candidateIndex = 0; candidateIndex < bucket.Count; candidateIndex++)
                {
                    var candidate = bucket[candidateIndex];
                    string? other = candidate.Signature;
                    if (!candidate.Computed)
                    {
                        if (signatures.TryGetValue(candidate.Block, out var previous))
                        {
                            other = previous;
                            response.FamilySignatureCacheHits++;
                        }
                        else
                        {
                            other = FamilySignature(candidate.Block);
                            signatures[candidate.Block] = other;
                            response.FamilySignaturesComputed++;
                        }
                        bucket[candidateIndex] = (candidate.Block, other, true);
                    }
                    if (other == signature) { same = candidate.Block; break; }
                }
                if (same != null)
                {
                    var replacement = new Insert(same) { InsertPoint = insert.InsertPoint, Normal = insert.Normal,
                        Rotation = insert.Rotation, XScale = insert.XScale, YScale = insert.YScale, ZScale = insert.ZScale };
                    replacement.MatchProperties(insert); insert = replacement; changed = true;
                }
                else
                {
                    bucket.Add((insert.Block, signature, true));
                    surviving[insert.Block.Name] = pair.Value;
                }
            }
            used.Add(insert.Block.Name);
            rebuilt.Add(insert);
        }
        if (changed)
        {
            document.ModelSpace.Entities.Clear();
            foreach (var entity in rebuilt) document.Entities.Add(entity);
            PreserveMaskDrawOrder(document.ModelSpace, rebuilt);
        }
        // Remove only unused definitions made by this operation, never source/user blocks.
        foreach (var block in document.BlockRecords.ToArray())
            if (!used.Contains(block.Name) && known.Keys.Any(k => block.Name.Contains(k, StringComparison.Ordinal)))
                document.BlockRecords.Remove(block.Name);
        response.FamilyBlocks = surviving;
        response.FamilyBlockDefinitions = used.Count;
        response.FamilySignaturesSkipped = response.FamilyBlockReferences - signatures.Count;
        response.TimingsMs["familyDedup"] = clock.Elapsed.TotalMilliseconds;
        if (response.FamilyBlockReferences > 0)
            response.Warnings.Add($"자동 블록: 배치 {response.FamilyBlockReferences:N0}개 · 공유 정의 {response.FamilyBlockDefinitions:N0}개 · 고정 형상 패밀리와 상세 그룹만 처리");
        foreach (var fallback in response.FamilyBlockFallbacks)
            response.Warnings.Add($"패밀리 개별 객체 유지: {fallback.Key} · {fallback.Value:N0}개");
    }

    // Cheap, deterministic prefilter only. A matching key never causes reuse by
    // itself; candidates still pass the existing full structural signature check.
    // Values derived from the full signature may collide here, which only performs
    // extra exact comparisons and cannot merge different geometry.
    private static string FamilyPrefilterSignature(BlockRecord block)
    {
        try
        {
            var text = new StringBuilder();
            var path = new HashSet<BlockRecord>();
            void Number(double value) => text.Append(value == 0 ? "0" : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
            void VisitBlock(BlockRecord current, int depth)
            {
                if (depth > 32 || !path.Add(current)) throw new NotSupportedException();
                try
                {
                    var entities = current.GetSortedEntities().ToArray();
                    text.Append('B').Append(entities.Length).Append('|');
                    if (entities.Length > 0)
                    {
                        Box bounds = Bounds(entities);
                        Number(bounds.MinX); Number(bounds.MinY); Number(bounds.MaxX); Number(bounds.MaxY);
                    }
                    foreach (Entity entity in entities)
                    {
                        text.Append(entity.ObjectName).Append('|').Append(entity.Layer.Name).Append('|').Append(entity.LineType.Name).Append('|');
                        if (entity is TextEntity single) text.Append(single.Value).Append('|');
                        if (entity is MText multiple) text.Append(multiple.Value).Append('|');
                        if (entity is Hatch hatch) text.Append(hatch.Pattern?.Name).Append('|').Append(hatch.Paths.Count).Append('|');
                        if (entity is LwPolyline polyline) text.Append(polyline.Vertices.Count).Append('|');
                        if (entity is Insert insert)
                        {
                            text.Append('A').Append(insert.Attributes.Count).Append('|');
                            VisitBlock(insert.Block, depth + 1);
                        }
                        if (entity is Dimension dimension && dimension.Block != null) VisitBlock(dimension.Block, depth + 1);
                    }
                }
                finally { path.Remove(current); }
            }
            VisitBlock(block, 0);
            return Hash(text.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { return "FULL"; }
    }

    // Exact structural comparison, no geometry rounding or positional tolerance. Unknown
    // metadata/geometry disables reuse; it does not discard the family or stop the DWG.
    private static string? FamilySignature(BlockRecord block)
    {
        try
        {
            var path = new HashSet<BlockRecord>();
            object BlockSnapshot(BlockRecord current, int depth)
            {
                if (depth > 32 || !path.Add(current)) throw new NotSupportedException();
                try { return current.GetSortedEntities().Select(e => EntitySnapshot(e, depth + 1)).ToArray(); }
                finally { path.Remove(current); }
            }
            object EntitySnapshot(Entity entity, int depth)
            {
                if (entity is not (Line or Arc or Circle or LwPolyline or Hatch or TextEntity or MText or Ellipse or Spline or Solid
                    or Insert or Dimension or AttributeEntity or AttributeDefinition)
                    || entity.XDictionary != null || entity.ExtendedData.Any() || entity.Reactors.Any() || entity.Material != null || entity.BookColor != null)
                    throw new NotSupportedException();
                var value = new SortedDictionary<string, object?> { ["entity"] = Snapshot(entity, depth) };
                if (entity is Insert insert)
                {
                    value["block"] = BlockSnapshot(insert.Block, depth + 1);
                    value["attributes"] = insert.Attributes.Select(a => Snapshot(a, depth + 1)).ToArray();
                }
                if (entity is Dimension dimension && dimension.Block != null)
                    value["display"] = BlockSnapshot(dimension.Block, depth + 1);
                return value;
            }
            return JsonSerializer.Serialize(BlockSnapshot(block, 0));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException or NotSupportedException) { return null; }
    }

    private static readonly HashSet<string> SignatureSkip = new() { "Handle", "Owner", "Document", "Name", "Reactors", "XDictionary", "ExtendedData",
        "Block", "Attributes", "Style", "Material", "Layer", "LineType", "BoundingBox", "CadObject", "Entities", "ShapeStyle", "PlotStyleName" };

    private static object? Snapshot(object? value, int depth)
    {
        if (value == null) return null;
        if (depth > 24) throw new NotSupportedException();
        var type = value.GetType();
        if (value is double d) return d == 0 ? "0" : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) return value.ToString();
        if (value is IEnumerable sequence) return sequence.Cast<object?>().Select(v => Snapshot(v, depth + 1)).ToArray();
        var result = new SortedDictionary<string, object?> { ["type"] = type.FullName };
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !SignatureSkip.Contains(p.Name)))
            result[property.Name] = Snapshot(property.GetValue(value), depth + 1);
        if (value is Entity e)
        {
            result["Layer"] = e.Layer.Name;
            result["LineType"] = e.LineType.Name;
            result["LinePattern"] = e.LineType.Segments.Select(SegmentSignature).Select(s => s.ToString()).ToArray();
        }
        if (value is TextEntity text) result["TextStyle"] = Snapshot(text.Style, depth + 1);
        if (value is MText mtext) result["TextStyle"] = Snapshot(mtext.Style, depth + 1);
        return result;
    }
}
