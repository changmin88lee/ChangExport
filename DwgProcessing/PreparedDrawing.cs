using ACadSharp;
using CSMath;
using System.Diagnostics;

namespace ChangExport.DwgProcessing;

// Detached managed DWG data only. Never stores or accesses a Revit API object.
public sealed class PreparedDrawing
{
    internal CadDocument Document { get; }
    internal bool Consumed { get; set; }
    public BridgeResponse Response { get; }
    public string Source { get; }
    internal PreparedDrawing(CadDocument document, BridgeResponse response, string source)
    { Document = document; Response = response; Source = source; }
}

public sealed partial class ManagedDwgProcessor
{
    public BridgeResponse MergePrepared(BridgeRequest request, IReadOnlyList<PreparedDrawing> drawings, string workingDirectory,
        Func<bool>? cancel = null, Action? pump = null)
    {
        if (drawings.Count == 0 || drawings.Any(d => d.Consumed) || drawings.Distinct().Count() != drawings.Count)
            throw new InvalidOperationException("병합할 메모리 도면이 없거나 이미 사용되었습니다.");
        if (!request.RevitSheet || request.Direction is not ("Horizontal" or "Vertical") || !double.IsFinite(request.MarginMm) || request.MarginMm < 0)
            throw new InvalidDataException("메모리 병합의 출력 방향·간격을 확인하세요.");
        var check = CreateCheck(cancel, pump); check();
        var clock = Stopwatch.StartNew();
        var response = new BridgeResponse { OutputPath = request.OutputPath };
        var geometry = new GeometryContext(request);
        foreach (var drawing in drawings)
        foreach (var pair in drawing.Response.FamilyBlocks) geometry.Families.TryAdd(pair.Key, pair.Value);
        request.Inputs = drawings.Select(d => d.Source).ToList();
        foreach (var drawing in drawings) drawing.Consumed = true;
        CadDocument document;
        if (drawings.Count == 1)
        {
            // Keep the same origin convention as Merge (vertical starts below Y=0).
            var source = drawings[0].Document;
            var box = Bounds(source.Entities);
            double y = request.Direction == "Vertical" ? -box.Height : 0;
            ApplyLayerStyles(source, request.LayerStyles);
            document = EditableModel(source, response, check, Transform.CreateTranslation(new XYZ(-box.MinX, y - box.MinY, 0)), geometry);
            response.Placements.Add(new SheetPlacement { Source = drawings[0].Source, X = 0, Y = y, Width = box.Width, Height = box.Height });
        }
        else document = EditableModel(Merge(request, response, check, drawings), response, check, geometry: geometry);
        if (request.UseLayerColors) NormalizeLayerColors(document, response, check);
        DeduplicateFamilies(document, response, geometry);
        response.TimingsMs["merge"] = clock.Elapsed.TotalMilliseconds;
        return SavePrepared(document, response, request.OutputPath, workingDirectory, check);
    }
}
