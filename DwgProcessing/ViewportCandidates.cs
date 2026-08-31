using ACadSharp.Entities;
using CSMath;

namespace ChangExport.DwgProcessing;

public sealed partial class ManagedDwgProcessor
{
    // Only reject provably outside straight linework. Unknown/text/curve/hatch bounds
    // never cause rejection; their existing exact clipping path remains unchanged.
    private static bool OutsideViewportLinework(Entity entity, Viewport viewport, Dictionary<Entity, Box?> cache)
    {
        if (viewport.Status.HasFlag(ViewportStatusFlags.NonRectangularClipping)) return false;
        var bounds = LineworkBox(entity, cache, 0);
        if (bounds is not { } b) return false;
        double angle = -viewport.TwistAngle, scale = viewport.ScaleFactor;
        XYZ target = Rotate(viewport.ViewTarget, angle);
        var points = new[] { new XYZ(b.MinX, b.MinY, 0), new XYZ(b.MaxX, b.MinY, 0),
            new XYZ(b.MinX, b.MaxY, 0), new XYZ(b.MaxX, b.MaxY, 0) }.Select(p => Rotate(p, angle) * scale
                + new XYZ(viewport.Center.X - scale * (viewport.ViewCenter.X + target.X),
                    viewport.Center.Y - scale * (viewport.ViewCenter.Y + target.Y), 0)).ToArray();
        return points.Max(p => p.X) < viewport.Center.X - viewport.Width / 2 - Epsilon
            || points.Min(p => p.X) > viewport.Center.X + viewport.Width / 2 + Epsilon
            || points.Max(p => p.Y) < viewport.Center.Y - viewport.Height / 2 - Epsilon
            || points.Min(p => p.Y) > viewport.Center.Y + viewport.Height / 2 + Epsilon;
    }

    private static Box? LineworkBox(Entity entity, Dictionary<Entity, Box?> cache, int depth)
    {
        if (cache.TryGetValue(entity, out var cached)) return cached;
        Box? result = null;
        if (entity is Line line && Math.Abs(line.StartPoint.Z) < Epsilon && Math.Abs(line.EndPoint.Z) < Epsilon)
            result = new Box(Math.Min(line.StartPoint.X, line.EndPoint.X), Math.Min(line.StartPoint.Y, line.EndPoint.Y),
                Math.Max(line.StartPoint.X, line.EndPoint.X), Math.Max(line.StartPoint.Y, line.EndPoint.Y));
        else if (entity is Insert insert && depth < 64 && !insert.IsMultiple && !insert.Block.IsDynamic
            && insert.Attributes.Count == 0 && insert.SpatialFilter == null && insert.Normal.DistanceFrom(XYZ.AxisZ) < Epsilon
            && Math.Abs(insert.InsertPoint.Z) < Epsilon && insert.XScale > 0 && Math.Abs(insert.XScale - insert.YScale) < Epsilon)
        {
            var boxes = insert.Block.Entities.Select(e => LineworkBox(e, cache, depth + 1)).ToList();
            if (boxes.Count > 0 && boxes.All(b => b.HasValue))
            {
                var transform = InsertTransform(insert);
                var points = boxes.SelectMany(b => new[] { new XYZ(b!.Value.MinX, b.Value.MinY, 0), new XYZ(b.Value.MinX, b.Value.MaxY, 0),
                    new XYZ(b.Value.MaxX, b.Value.MinY, 0), new XYZ(b.Value.MaxX, b.Value.MaxY, 0) }).Select(p => transform.ApplyTransform(p)).ToList();
                result = new Box(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
            }
        }
        cache[entity] = result;
        return result;
    }
}
