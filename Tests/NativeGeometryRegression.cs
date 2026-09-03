using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using CSMath;

internal static class NativeGeometryRegression
{
    public static void Run(string output, Action<bool, string> check)
    {
        CadDocument native = DwgRegression.Sheet();
        var nativeType = new Layer("NATIVE-TYPE") { Color = new ACadSharp.Color(7) };
        var nativeKeep = new Layer("NATIVE-KEEP") { Color = new ACadSharp.Color(7) };
        native.Layers.Add(nativeType); native.Layers.Add(nativeKeep);
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 300, 0), EndPoint = new XYZ(450, 300, 0), Layer = nativeType });
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 305, 0), EndPoint = new XYZ(450, 305, 0), Layer = nativeKeep });
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 320, 0), EndPoint = new XYZ(450, 320, 0), Layer = nativeKeep });
        var linkedTriangle = new Hatch { IsSolid = true, Color = new ACadSharp.Color(255, 0, 0), Layer = nativeKeep };
        linkedTriangle.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Polyline(new[] { new XYZ(500, 300, 0), new XYZ(550, 350, 0), new XYZ(600, 300, 0) })
        }));
        native.Entities.Add(linkedTriangle);

        CadDocument filtered = DwgRegression.Sheet();
        var marker = new Layer("REVIT-FILTER") { Color = new ACadSharp.Color(7) };
        filtered.Layers.Add(marker);
        filtered.Entities.Add(new Line { StartPoint = new XYZ(300, 300, 0), EndPoint = new XYZ(450, 300, 0),
            Layer = marker, Color = new ACadSharp.Color(200) });
        // A Part-only material boundary near native geometry must never replace it.
        filtered.Entities.Add(new Line { StartPoint = new XYZ(300, 310, 0), EndPoint = new XYZ(450, 310, 0),
            Layer = marker, Color = new ACadSharp.Color(201) });
        // Revit Parts can split one native boundary into multiple collinear pieces.
        filtered.Entities.Add(new Line { StartPoint = new XYZ(300, 320, 0), EndPoint = new XYZ(360, 320, 0),
            Layer = marker, Color = new ACadSharp.Color(201) });
        filtered.Entities.Add(new Line { StartPoint = new XYZ(360, 320, 0), EndPoint = new XYZ(450, 320, 0),
            Layer = marker, Color = new ACadSharp.Color(201) });

        string nativePath = Path.Combine(output, "nge-native-source.dwg");
        string filteredPath = Path.Combine(output, "nge-filter-source.dwg");
        string resultPath = Path.Combine(output, "nge-final.dwg");
        DwgWriter.Write(nativePath, native); DwgWriter.Write(filteredPath, filtered);
        var response = new ManagedDwgProcessor().Run(new BridgeRequest
        {
            Operation = "Flatten",
            OutputPath = resultPath,
            FilterReferencePath = filteredPath,
            UseLayerColors = true,
            ColorRemaps = new()
            {
                new() { MarkerAci = 200, Layer = "TYPE-FILTER", Color = 3, RuleId = "type" },
                new() { MarkerAci = 201, Layer = "MATERIAL-FILTER", Color = 4, RuleId = "material", RemapFills = true,
                    BoundaryPriority = 700, SourceLayers = new() { "NATIVE-KEEP" } }
            }
        }, nativePath, output);
        CadDocument saved = DwgReader.Read(resultPath);
        Entity[] entities = DwgRegression.Walk(saved.ModelSpace).ToArray();
        check(response.GeometrySource == "NativeGeometry" && response.NativeOverlayMatchedEntities == 2,
            "NGE reports the native drawing as final geometry and transfers exact and fully covered classifications");
        check(entities.OfType<Line>().Count(line => line.Layer.Name == "TYPE-FILTER") == 1,
            "Exact type-filter geometry is relayered on the native entity");
        check(entities.Count(entity => entity.Layer.Name == "MATERIAL-FILTER") == 1
            && response.NativeOverlayUnmatchedMarkers >= 1,
            "Collinear Part segments can classify one native line while Part-only geometry is not added");
        check(entities.OfType<Line>().Any(line => line.Layer.Name == "NATIVE-KEEP"),
            "Nearby native door/floor geometry is not captured by a non-exact material marker");
        check(entities.OfType<Hatch>().Any(hatch => hatch.Color.R == 255 && hatch.Color.G == 0 && hatch.Color.B == 0),
            "Native linked hatch remains even when the filtered drawing omits it");
    }
}
