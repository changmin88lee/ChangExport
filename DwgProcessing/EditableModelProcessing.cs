using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;

namespace ChangExport.DwgProcessing;

public sealed partial class ManagedDwgProcessor
{
    private static double RevitModelScale(CadDocument source, List<string> warnings)
    {
        var views = source.Layouts.Where(l => l.IsPaperSpace).SelectMany(l => l.AssociatedBlock.Entities)
            .OfType<Viewport>().Where(v => IsEnabledViewport(v) && !OmitViewport(v) && v.ScaleFactor > 0)
            .OrderByDescending(v => v.Width * v.Height).ToList();
        if (views.Count == 0)
        {
            warnings.Add("축척: 기준 2D 뷰가 없어 시트 지면 크기(1:1)를 유지했습니다.");
            return 1;
        }
        double scale = 1 / views[0].ScaleFactor;
        if (Math.Abs(scale - Math.Round(scale)) < 1e-6) scale = Math.Round(scale);
        if (!double.IsFinite(scale) || scale <= 0) throw new InvalidDataException("시트 기준 축척이 올바르지 않습니다.");
        bool mixed = views.Any(v => Math.Abs(1 / v.ScaleFactor - scale) > 1e-6);
        warnings.Add($"축척: 시트·도곽을 {scale:G}배 확대하여 모형공간에 배치했습니다."
            + (mixed ? " 혼합 축척 시트는 가장 큰 뷰 영역을 기준으로 하며 다른 뷰의 상대 축척은 유지됩니다." : " 기준 뷰의 도형은 실물 크기입니다."));
        return scale;
    }

    // Work on detached clones only. The source RVT, staging DWGs and existing outputs
    // are never edited. Dimensions keep their native entity and display definition.
    private static CadDocument EditableModel(CadDocument source, BridgeResponse response, Action check, Transform? placement = null, GeometryContext? geometry = null)
    {
        var target = CreateOutput(source);
        int dimensionIndex = 0, clipIndex = 0;
        var retained = new Dictionary<string, int>();
        var initial = placement ?? Transform.CreateScaling(new XYZ(response.ModelScale));
        var output = new List<Entity>();

        void Add(Entity original, Transform transform, List<List<XY>> clips, Entity? parent, int depth, bool familyMember = false, NativeLineDisplay? parentDisplay = null)
        {
            check();
            var nativeDisplay = ResolveNativeDisplay(original, parentDisplay, geometry);
            if (depth > 64) throw new InvalidDataException("블록 깊이가 안전 범위를 초과했습니다.");
            // An ordinary INSERT is only a transform/property context. Do not deep-clone
            // its entire block before visiting (and cloning) each leaf separately.
            Entity entity = original is Insert context ? InsertContext(context) : (Entity)original.Clone();
            InheritDisplay(entity, parent);
            if (entity.IsInvisible || !entity.Layer.IsOn || entity.Layer.Flags.HasFlag(LayerFlags.Frozen)) return;
            PreserveRevitFillAppearance(entity, parent, response);
            if (entity is Insert insert)
            {
                var sourceInsert = (Insert)original;
                var family = FamilyInfo(sourceInsert.Block.Name, geometry);
                if (family is { Processed: true } && clips.Count == 0)
                {
                    var clone = (Insert)original.Clone(); InheritDisplay(clone, parent);
                    PreserveNestedRevitFillAppearance(clone, parent, response, new HashSet<BlockRecord>());
                    var ready = PlaceFamily(clone.Block, new Transform(transform.Matrix * InsertTransform(clone).Matrix));
                    ready.MatchProperties(clone);
                    if (clone.SpatialFilter is { } spatialFilter)
                        ready.SpatialFilter = (SpatialFilter)spatialFilter.Clone();
                    CapturePreparedWideColors(ready, geometry);
                    output.Add(ready); return;
                }
                bool planarSimpleMirror = Math.Abs(Math.Abs(insert.XScale) - Math.Abs(insert.YScale)) < Epsilon
                    && Math.Abs(Math.Abs(insert.Normal.Z) - 1) < Epsilon
                    && sourceInsert.Block.Entities.All(e => e is Line or Hatch { IsSolid: true });
                if (!planarSimpleMirror && (Math.Abs(insert.XScale - insert.YScale) > Epsilon || insert.XScale <= 0 || insert.YScale <= 0
                    || insert.Normal.DistanceFrom(XYZ.AxisZ) > Epsilon))
                {
                    insert = (Insert)original.Clone();
                    InheritDisplay(insert, parent);
                    PreserveNestedRevitFillAppearance(insert, parent, response, new HashSet<BlockRecord>());
                    // Nonuniform/mirrored/tilted block transforms can turn circles into
                    // ellipses or shear nested geometry. Keep only this exceptional block.
                    insert.ApplyTransform(transform);
                    Entity preserved = insert;
                    foreach (var polygon in clips)
                    {
                        var wrapper = new BlockRecord("CE_BOUNDARY_" + ++clipIndex); wrapper.Entities.Add(preserved);
                        preserved = new Insert(wrapper) { SpatialFilter = new SpatialFilter(SpatialFilter.SpatialFilterEntryName)
                        { Origin = XYZ.Zero, Normal = XYZ.AxisZ, DisplayBoundary = true, BoundaryPoints = polygon } };
                    }
                    CapturePreparedWideColors(preserved, geometry);
                    output.Add(preserved);
                    if (geometry?.Request.WideLineLayers.Count > 0)
                    {
                        string skipped = "전역폭 확인 필요: 비균등·반전·기울어진 보존 블록 내부 선은 원본을 유지했습니다.";
                        if (!response.Warnings.Contains(skipped)) response.Warnings.Add(skipped);
                    }
                    string warning = "블록 표현 보존: 비균등 축척·반전·기울어진 블록은 형상 손상을 막기 위해 원래 블록을 유지했습니다.";
                    if (!response.Warnings.Contains(warning)) response.Warnings.Add(warning);
                    return;
                }
                var local = InsertTransform(insert);
                var combined = new Transform(transform.Matrix * local.Matrix);
                var activeClips = new List<List<XY>>(clips);
                if (insert.SpatialFilter is { DisplayBoundary: true } filter)
                    activeClips.Add(filter.BoundaryPoints.Select(p => combined.ApplyTransform(new XYZ(p.X, p.Y, 0)))
                        .Select(p => new XY(p.X, p.Y)).ToList());
                response.ExplodedInserts++;
                int familyStart = output.Count;
                foreach (Entity child in sourceInsert.Block.GetSortedEntities()) Add(child, combined, activeClips, insert, depth + 1, familyMember || family != null, nativeDisplay);
                if (!familyMember && family is { Processed: false })
                    GroupFamily(output, familyStart, combined, family, response, geometry!, activeClips);
                // Attribute positions are already in the enclosing insert's coordinates.
                foreach (AttributeEntity attribute in sourceInsert.Attributes)
                {
                    if (attribute.Flags.HasFlag(AttributeFlags.Hidden)) continue;
                    if (sourceInsert.Block.Entities.OfType<AttributeDefinition>().Any(d => d.Tag == attribute.Tag && d.Flags.HasFlag(AttributeFlags.Constant))) continue;
                    Add(AttributeText(attribute), transform, activeClips, insert, depth + 1);
                }
                return;
            }
            if (entity is AttributeDefinition definition)
            {
                if (!definition.Flags.HasFlag(AttributeFlags.Constant) || definition.Flags.HasFlag(AttributeFlags.Hidden)) return;
                entity = AttributeText(definition);
            }
            TransformEditable(entity, transform, ref dimensionIndex);
            if (IsPreparedWideColor(original, geometry)) geometry!.WideColorEntities.Add(entity);
            var wide = WidthSource(entity, nativeDisplay, geometry);
            if (clips.Count == 0) { output.Add(MakeWideLine(entity, wide, response.ModelScale, response, geometry)); return; }
            if (entity is Line line)
            {
                foreach (Line segment in ClipLine(line, clips)) output.Add(MakeWideLine(segment, wide, response.ModelScale, response, geometry));
                return;
            }
            // A text, hatch or curved entity cannot be classified safely with an
            // axis-aligned envelope. Preserve it under the exact viewport boundary.
            string kind = entity.ObjectName;
            entity = MakeWideLine(entity, wide, response.ModelScale, response, geometry);
            retained[kind] = retained.GetValueOrDefault(kind) + 1;
            foreach (var polygon in clips)
            {
                var block = new BlockRecord("CE_BOUNDARY_" + ++clipIndex);
                block.Entities.Add(entity);
                entity = new Insert(block)
                {
                    SpatialFilter = new SpatialFilter(SpatialFilter.SpatialFilterEntryName)
                    { Origin = XYZ.Zero, Normal = XYZ.AxisZ, DisplayBoundary = true, BoundaryPoints = polygon }
                };
                response.BoundaryBlocksRetained++;
            }
            output.Add(entity);
        }

        foreach (Entity entity in source.ModelSpace.GetSortedEntities()) Add(entity, initial, new(), null, 0);
        foreach (var entity in output) target.Entities.Add(entity);
        PreserveMaskDrawOrder(target.ModelSpace, output);
        if (retained.Count > 0)
            response.Warnings.Add("경계 표현 보존: 뷰포트 경계가 적용된 " + string.Join(", ", retained.Select(p => $"{p.Key} {p.Value}개"))
                + "는 해당 객체만 정확한 잘림 경계로 유지했습니다. 일반 선과 시트 전체는 블록으로 묶지 않습니다.");
        SetExtents(target);
        return target;
    }

    private static Insert InsertContext(Insert source)
    {
        var frame = new BlockRecord("CE_CONTEXT"); frame.BlockEntity.BasePoint = source.Block.BlockEntity.BasePoint;
        var result = new Insert(frame) { InsertPoint = source.InsertPoint, Normal = source.Normal,
            XScale = source.XScale, YScale = source.YScale, ZScale = source.ZScale, Rotation = source.Rotation };
        result.MatchProperties(source);
        if (source.SpatialFilter is { } filter) result.SpatialFilter = (SpatialFilter)filter.Clone();
        return result;
    }

    private static Transform InsertTransform(Insert insert)
    {
        // Translate the base point BEFORE scaling/rotation (the SDK's GetTransform
        // subtracts it after scaling, which is incorrect for nonzero block origins).
        var world = Matrix4.GetArbitraryAxis(insert.Normal);
        return new Transform(world * Transform.CreateTranslation(insert.InsertPoint).Matrix
            * Transform.CreateRotation(XYZ.AxisZ, insert.Rotation).Matrix
            * Transform.CreateScaling(new XYZ(insert.XScale, insert.YScale, insert.ZScale)).Matrix
            * Transform.CreateTranslation(-insert.Block.BlockEntity.BasePoint).Matrix);
    }

    private static Entity AttributeText(AttributeBase source)
    {
        if (source.MText != null)
        {
            var multiline = (MText)source.MText.Clone(); multiline.MatchProperties(source); return multiline;
        }
        var text = new TextEntity { Value = source.Value, InsertPoint = source.InsertPoint, AlignmentPoint = source.AlignmentPoint,
            Height = source.Height, Rotation = source.Rotation, WidthFactor = source.WidthFactor, ObliqueAngle = source.ObliqueAngle,
            Normal = source.Normal, Style = source.Style, HorizontalAlignment = source.HorizontalAlignment, VerticalAlignment = source.VerticalAlignment };
        text.MatchProperties(source);
        return text;
    }

    private static void InheritDisplay(Entity entity, Entity? parent)
    {
        if (parent == null) return;
        if (entity.Layer.Name == "0") entity.Layer = parent.Layer;
        if (entity.Color.IsByBlock) entity.Color = parent.Color.IsByLayer ? parent.Layer.Color : parent.Color;
        if (entity.LineType.Name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
            entity.LineType = parent.LineType.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ? parent.Layer.LineType : parent.LineType;
        if (entity.LineWeight == LineWeightType.ByBlock)
            entity.LineWeight = parent.LineWeight == LineWeightType.ByLayer ? parent.Layer.LineWeight : parent.LineWeight;
    }

    private static void TransformEditable(Entity entity, Transform transform, ref int dimensionIndex)
    {
        double factor = Vector(transform, XYZ.AxisX).GetLength();
        TransformGeometry(entity, transform);
        entity.LineTypeScale *= factor;
        if (entity is not Dimension dimension) return;
        dimension.Style.Name = "CE_DIM_" + ++dimensionIndex + "_" + dimension.Style.Name;
        dimension.Style.ScaleFactor *= factor;
        dimension.Style.LinearScaleFactor /= factor;
        if (dimension.Block == null) return;
        // Imported *D names contain source handles. Never let registration reuse a
        // different dimension's display block before the SDK assigns its new handle.
        dimension.Block.Name = "CE_DIM_DISPLAY_" + dimensionIndex;
        foreach (Entity child in dimension.Block.Entities)
        {
            InheritDisplay(child, dimension);
            TransformGeometry(child, transform);
            child.LineTypeScale *= factor;
        }
    }

    private static XYZ Vector(Transform transform, XYZ vector) => transform.ApplyTransform(vector) - transform.ApplyTransform(XYZ.Zero);

    private static void TransformGeometry(Entity entity, Transform transform)
    {
        // ACadSharp 3.7.1 treats some radius/axis/tangent vectors as points, adding
        // translation to them. Preserve those quantities explicitly while exploding.
        if (entity is Hatch hatch)
        {
            var edges = hatch.Paths.Select(p => p.Edges.Select(e => e.Clone()).ToList()).ToList();
            var patternLines = hatch.Pattern?.Lines.Select(line => new
            {
                line.Angle,
                line.BasePoint,
                line.Offset,
                DashLengths = line.DashLengths.ToList()
            }).ToList();
            // Solid boundaries can be expressed directly in the final XY plane,
            // including Revit's mirrored blocks with a -Z extrusion normal.
            var edgeTransform = hatch.IsSolid ? new Transform(transform.Matrix * Matrix4.GetArbitraryAxis(hatch.Normal)) : transform;
            hatch.ApplyTransform(transform);
            // ACadSharp 3.7.1 applies translation to HatchPattern.Line.Offset as
            // though it were a point. Offset is a repeat vector: translating it
            // makes ordinary Revit fills repeat tens of metres apart after a
            // viewport is flattened. Rebuild every patterned line from its
            // original point/vector semantics.
            if (!hatch.IsSolid && patternLines != null && hatch.Pattern != null
                && hatch.Pattern.Lines.Count == patternLines.Count)
            {
                for (int lineIndex = 0; lineIndex < patternLines.Count; lineIndex++)
                {
                    var original = patternLines[lineIndex];
                    var line = hatch.Pattern.Lines[lineIndex];
                    XYZ basePoint = transform.ApplyTransform(new XYZ(original.BasePoint.X, original.BasePoint.Y, 0));
                    XYZ offset = Vector(transform, new XYZ(original.Offset.X, original.Offset.Y, 0));
                    XYZ direction = Vector(transform, new XYZ(Math.Cos(original.Angle), Math.Sin(original.Angle), 0));
                    double dashScale = direction.GetLength();
                    line.BasePoint = new XY(basePoint.X, basePoint.Y);
                    line.Offset = new XY(offset.X, offset.Y);
                    line.Angle = Math.Atan2(direction.Y, direction.X);
                    line.DashLengths.Clear();
                    line.DashLengths.AddRange(original.DashLengths.Select(length => length * dashScale));
                }
            }
            if (hatch.IsSolid) hatch.Normal = XYZ.AxisZ;
            hatch.IsAssociative = false;
            transform = edgeTransform;
            for (int i = 0; i < hatch.Paths.Count; i++)
            {
                var path = hatch.Paths[i];
                path.Edges.Clear(); path.Entities.Clear();
                foreach (var edge in edges[i])
                {
                    if (edge is Hatch.BoundaryPath.Arc arc)
                    {
                        bool full = Math.Abs(Math.Abs(arc.EndAngle - arc.StartAngle) - 2 * Math.PI) < Epsilon;
                        bool ccw = arc.CounterClockWise;
                        var start = Vector(transform, new XYZ(Math.Cos(arc.StartAngle), Math.Sin(ccw ? arc.StartAngle : -arc.StartAngle), 0));
                        var end = Vector(transform, new XYZ(Math.Cos(arc.EndAngle), Math.Sin(ccw ? arc.EndAngle : -arc.EndAngle), 0));
                        XYZ center = transform.ApplyTransform(new XYZ(arc.Center.X, arc.Center.Y, 0));
                        arc.Center = new XY(center.X, center.Y);
                        arc.Radius *= Vector(transform, XYZ.AxisX).GetLength();
                        bool reflected = XYZ.Cross(Vector(transform, XYZ.AxisX), Vector(transform, XYZ.AxisY)).Z < 0;
                        arc.CounterClockWise = ccw != reflected;
                        double sign = arc.CounterClockWise ? 1 : -1;
                        arc.StartAngle = sign * Math.Atan2(start.Y, start.X);
                        arc.EndAngle = full ? arc.StartAngle + 2 * Math.PI : sign * Math.Atan2(end.Y, end.X);
                    }
                    else if (edge is Hatch.BoundaryPath.Ellipse ellipse)
                    {
                        XYZ center = transform.ApplyTransform(new XYZ(ellipse.Center.X, ellipse.Center.Y, 0));
                        XYZ axis = Vector(transform, new XYZ(ellipse.MajorAxisEndPoint.X, ellipse.MajorAxisEndPoint.Y, 0));
                        ellipse.Center = new XY(center.X, center.Y); ellipse.MajorAxisEndPoint = new XY(axis.X, axis.Y);
                    }
                    else edge.ApplyTransform(transform);
                    path.Edges.Add(edge);
                }
            }
        }
        else if (entity is Arc arc && arc.Normal.DistanceFrom(XYZ.AxisZ) < Epsilon
            && Math.Abs(Vector(transform, XYZ.AxisX).Z) < Epsilon && Math.Abs(Vector(transform, XYZ.AxisY).Z) < Epsilon
            && XYZ.Cross(Vector(transform, XYZ.AxisX), Vector(transform, XYZ.AxisY)).Z > 0)
        {
            // ACadSharp 3.7.1 rotates planar ARC angles in the opposite direction
            // to their centers. Transform the original radius directions forward,
            // just like the door's lines. Keep the existing nonplanar/mirror path.
            bool full = Math.Abs(Math.Abs(arc.EndAngle - arc.StartAngle) - 2 * Math.PI) < Epsilon;
            XYZ start = Vector(transform, new XYZ(Math.Cos(arc.StartAngle), Math.Sin(arc.StartAngle), 0));
            XYZ end = Vector(transform, new XYZ(Math.Cos(arc.EndAngle), Math.Sin(arc.EndAngle), 0));
            arc.ApplyTransform(transform);
            arc.StartAngle = Math.Atan2(start.Y, start.X);
            arc.EndAngle = full ? arc.StartAngle + 2 * Math.PI : Math.Atan2(end.Y, end.X);
        }
        else if (entity is Ellipse ellipse)
        {
            XYZ minor = XYZ.Cross(ellipse.Normal, ellipse.MajorAxisEndPoint) * ellipse.RadiusRatio;
            ellipse.Center = transform.ApplyTransform(ellipse.Center);
            ellipse.MajorAxisEndPoint = Vector(transform, ellipse.MajorAxisEndPoint);
            ellipse.Normal = XYZ.Cross(ellipse.MajorAxisEndPoint, Vector(transform, minor)).Normalize();
        }
        else if (entity is Spline spline)
        {
            XYZ start = Vector(transform, spline.StartTangent), end = Vector(transform, spline.EndTangent);
            spline.ApplyTransform(transform); spline.StartTangent = start; spline.EndTangent = end;
        }
        else if (entity is DimensionLinear dim)
        {
            XYZ axis = Vector(transform, new XYZ(Math.Cos(dim.Rotation), Math.Sin(dim.Rotation), 0));
            dim.ApplyTransform(transform); dim.Rotation = Math.Atan2(axis.Y, axis.X);
        }
        else entity.ApplyTransform(transform);
    }

    private static IEnumerable<Line> ClipLine(Line source, List<List<XY>> polygons)
    {
        var intervals = new List<(double A, double B)> { (0, 1) };
        XYZ delta = source.EndPoint - source.StartPoint;
        foreach (var polygon in polygons)
        {
            var cuts = new List<double> { 0, 1 };
            for (int i = 0; i < polygon.Count; i++)
            {
                XY a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                double ex = b.X - a.X, ey = b.Y - a.Y;
                double cross = delta.X * ey - delta.Y * ex;
                if (Math.Abs(cross) < Epsilon) continue;
                double ax = a.X - source.StartPoint.X, ay = a.Y - source.StartPoint.Y;
                double t = (ax * ey - ay * ex) / cross, u = (ax * delta.Y - ay * delta.X) / cross;
                if (t > 0 && t < 1 && u >= -Epsilon && u <= 1 + Epsilon) cuts.Add(t);
            }
            cuts.Sort();
            var next = new List<(double, double)>();
            for (int i = 1; i < cuts.Count; i++)
            {
                if (cuts[i] - cuts[i - 1] < Epsilon) continue;
                XYZ midpoint = source.StartPoint + delta * ((cuts[i] + cuts[i - 1]) / 2);
                if (!PointInside(new XY(midpoint.X, midpoint.Y), polygon)) continue;
                foreach (var range in intervals)
                {
                    double a = Math.Max(range.A, cuts[i - 1]), b = Math.Min(range.B, cuts[i]);
                    if (b - a > Epsilon) next.Add((a, b));
                }
            }
            intervals = next;
        }
        foreach (var range in intervals)
        {
            var line = (Line)source.Clone();
            line.StartPoint = source.StartPoint + delta * range.A;
            line.EndPoint = source.StartPoint + delta * range.B;
            yield return line;
        }
    }

    private static bool PointInside(XY p, List<XY> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            XY a = polygon[j], b = polygon[i];
            double cross = (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);
            if (Math.Abs(cross) <= Epsilon * Math.Max(1, Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y))
                && p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon
                && p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon) return true;
            if ((a.Y > p.Y) != (b.Y > p.Y)
                && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

}
