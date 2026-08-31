using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;

namespace ChangExport.DwgProcessing;

public sealed partial class ManagedDwgProcessor
{
    private sealed record WideLineSource(string Style, double PaperMm);
    private sealed record NativeLineDisplay(string Layer, LineWeightType LayerWeight, LineWeightType Weight);
    private sealed class GeometryContext
    {
        public BridgeRequest Request { get; }
        public Dictionary<Entity, NativeLineDisplay> NativeDisplays { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, FamilyBlockInfo> Families { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<FamilyBlockSource>> FamilyIndex { get; } = new(StringComparer.Ordinal);
        public GeometryContext(BridgeRequest request)
        {
            Request = request;
            foreach (var family in request.FamilySources)
            foreach (var prefix in family.NativePrefixes)
            {
                string key = FamilyKey(prefix);
                if (!FamilyIndex.TryGetValue(key, out var matches)) FamilyIndex[key] = matches = new();
                if (!matches.Contains(family)) matches.Add(family);
            }
        }
    }

    private static WideLineLayer? WideLayer(string name, IEnumerable<WideLineLayer> layers) => layers.FirstOrDefault(l =>
        name.Equals(l.NativeLayer, StringComparison.OrdinalIgnoreCase) || name.EndsWith("|" + l.NativeLayer, StringComparison.OrdinalIgnoreCase));

    private static void CaptureWidths(Entity root, GeometryContext? context)
    {
        if (context == null || context.Request.WideLineLayers.Count == 0) return;
        var seen = new HashSet<BlockRecord>();
        void Visit(Entity e, int depth)
        {
            if (depth > 64) return;
            context.NativeDisplays[e] = new(e.Layer.Name, e.Layer.LineWeight, e.LineWeight);
            if (e is Insert insert && seen.Add(insert.Block))
            {
                foreach (var child in insert.Block.Entities) Visit(child, depth + 1);
            }
        }
        Visit(root, 0);
    }

    private static NativeLineDisplay? ResolveNativeDisplay(Entity original, NativeLineDisplay? parent, GeometryContext? geometry)
    {
        var raw = geometry?.NativeDisplays.GetValueOrDefault(original);
        if (raw == null) return null;
        string layer = raw.Layer == "0" && parent != null ? parent.Layer : raw.Layer;
        var layerWeight = raw.Layer == "0" && parent != null ? parent.LayerWeight : raw.LayerWeight;
        var weight = raw.Weight == LineWeightType.ByBlock ? parent?.Weight ?? LineWeightType.Default : raw.Weight;
        if (weight == LineWeightType.ByLayer) weight = layerWeight;
        return new(layer, layerWeight, weight);
    }

    private static WideLineSource? WidthSource(Entity entity, NativeLineDisplay? display, GeometryContext? geometry)
    {
        if (display == null || geometry == null || entity is not (Line or Arc or Circle or LwPolyline or Polyline2D or Spline or Ellipse)) return null;
        var rule = WideLayer(display.Layer, geometry.Request.WideLineLayers);
        return rule == null ? null : new(rule.StyleName, (short)display.Weight > 0 ? (short)display.Weight / 100d : 0);
    }

    private static void RestoreWideLayers(CadDocument document, GeometryContext context)
    {
        if (context.Request.WideLineLayers.Count == 0) return;
        foreach (var block in document.BlockRecords.ToArray())
        foreach (var entity in block.Entities)
        {
            var rule = WideLayer(entity.Layer.Name, context.Request.WideLineLayers);
            if (rule == null) continue;
            if (!document.Layers.TryGetValue(rule.TargetLayer, out Layer layer))
            { layer = (Layer)entity.Layer.Clone(); layer.Name = rule.TargetLayer; document.Layers.Add(layer); }
            entity.Layer = layer;
        }
        foreach (var layer in document.Layers.Where(l => WideLayer(l.Name, context.Request.WideLineLayers) != null).ToArray())
            document.Layers.Remove(layer.Name);
    }

    private static Entity MakeWideLine(Entity entity, WideLineSource? source, double sheetScale, BridgeResponse response)
    {
        if (source == null) return entity;
        double width = source.PaperMm * sheetScale;
        LwPolyline? poly = null;
        if (width > 0 && double.IsFinite(width))
        {
            if (entity is Line line && Math.Abs(line.StartPoint.Z - line.EndPoint.Z) < Epsilon && Math.Abs(line.Thickness) < Epsilon)
                poly = new LwPolyline(new[] { new LwPolyline.Vertex(new XY(line.StartPoint.X, line.StartPoint.Y)),
                    new LwPolyline.Vertex(new XY(line.EndPoint.X, line.EndPoint.Y)) }) { Elevation = line.StartPoint.Z };
            else if (entity is Arc arc && arc.Normal.DistanceFrom(XYZ.AxisZ) < Epsilon && Math.Abs(arc.Thickness) < Epsilon)
            {
                double sweep = (arc.EndAngle - arc.StartAngle) % (2 * Math.PI); if (sweep <= 0) sweep += 2 * Math.PI;
                XY Point(double a) => new(arc.Center.X + arc.Radius * Math.Cos(a), arc.Center.Y + arc.Radius * Math.Sin(a));
                poly = new LwPolyline(new[] { new LwPolyline.Vertex(Point(arc.StartAngle)) { Bulge = Math.Tan(sweep / 4) },
                    new LwPolyline.Vertex(Point(arc.EndAngle)) }) { Elevation = arc.Center.Z };
            }
            else if (entity is Circle circle && circle.Normal.DistanceFrom(XYZ.AxisZ) < Epsilon && Math.Abs(circle.Thickness) < Epsilon)
                poly = new LwPolyline(new[] { new LwPolyline.Vertex(new XY(circle.Center.X + circle.Radius, circle.Center.Y)) { Bulge = 1 },
                    new LwPolyline.Vertex(new XY(circle.Center.X - circle.Radius, circle.Center.Y)) { Bulge = 1 } })
                    { IsClosed = true, Elevation = circle.Center.Z };
            else if (entity is LwPolyline existing && existing.Normal.DistanceFrom(XYZ.AxisZ) < Epsilon && Math.Abs(existing.Thickness) < Epsilon)
                poly = existing;
        }
        if (poly == null)
        {
            response.WideLineSkipped++;
            string warning = $"전역폭 미변환: '{source.Style}' · {(width <= 0 ? "유효한 원본 선굵기가 없음" : entity.ObjectName + " 곡선/방향은 현재 변환 미지원")} · 원본 객체를 유지했습니다.";
            if (!response.Warnings.Contains(warning)) response.Warnings.Add(warning);
            return entity;
        }
        if (!ReferenceEquals(poly, entity)) poly.MatchProperties(entity);
        poly.ConstantWidth = width;
        foreach (var vertex in poly.Vertices) vertex.StartWidth = vertex.EndWidth = 0;
        // Width is geometric; avoid applying an additional display/plot lineweight.
        poly.LineWeight = LineWeightType.W0;
        response.WideLineConverted++;
        response.WideLineStyleCounts[source.Style] = response.WideLineStyleCounts.GetValueOrDefault(source.Style) + 1;
        return poly;
    }
}
