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
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 340, 0), EndPoint = new XYZ(450, 340, 0), Layer = nativeKeep });
        var linkedTriangle = new Hatch { IsSolid = true, Color = new ACadSharp.Color(255, 0, 0), Layer = nativeKeep };
        linkedTriangle.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Polyline(new[] { new XYZ(500, 300, 0), new XYZ(550, 350, 0), new XYZ(600, 300, 0) })
        }));
        native.Entities.Add(linkedTriangle);
        // Same old bounding box and edge type, but a different boundary. It must
        // not inherit the first triangle's material classification.
        var differentTriangle = new Hatch { IsSolid = true, Color = new ACadSharp.Color(0, 0, 255), Layer = nativeKeep };
        differentTriangle.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Polyline(new[] { new XYZ(500, 300, 0), new XYZ(500, 350, 0), new XYZ(600, 300, 0) })
        }));
        native.Entities.Add(differentTriangle);

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
        // A compound Part can cover only one interval of a longer native wall line.
        filtered.Entities.Add(new Line { StartPoint = new XYZ(300, 340, 0), EndPoint = new XYZ(360, 340, 0),
            Layer = marker, Color = new ACadSharp.Color(201) });
        var filteredTriangle = (Hatch)linkedTriangle.Clone();
        filteredTriangle.Layer = marker; filteredTriangle.Color = new ACadSharp.Color(201);
        filtered.Entities.Add(filteredTriangle);

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
        check(response.GeometrySource == "NativeGeometry" && response.NativeOverlayMatchedEntities == 4
            && response.NativeOverlayPartialLinesSplit == 1,
            "NGE transfers exact, fully covered, partially covered and full-boundary hatch classifications");
        check(entities.OfType<Line>().Count(line => line.Layer.Name == "TYPE-FILTER") == 1,
            "Exact type-filter geometry is relayered on the native entity");
        check(entities.Count(entity => entity.Layer.Name == "MATERIAL-FILTER") == 3
            && response.NativeOverlayUnmatchedMarkers >= 1,
            "Collinear Part segments, a partial interval and an exact hatch classify only native geometry");
        check(entities.OfType<Line>().Any(line => line.Layer.Name == "MATERIAL-FILTER"
                && Math.Abs(line.StartPoint.Y - 340) < 1e-8 && Math.Abs(line.StartPoint.X - 300) < 1e-8
                && Math.Abs(line.EndPoint.X - 360) < 1e-8)
            && entities.OfType<Line>().Any(line => line.Layer.Name == "NATIVE-KEEP"
                && Math.Abs(line.StartPoint.Y - 340) < 1e-8 && Math.Abs(line.StartPoint.X - 360) < 1e-8
                && Math.Abs(line.EndPoint.X - 450) < 1e-8),
            "A partially covered native wall line is split only at the unambiguous Part boundary");
        check(entities.OfType<Line>().Any(line => line.Layer.Name == "NATIVE-KEEP"),
            "Nearby native door/floor geometry is not captured by a non-exact material marker");
        check(entities.OfType<Hatch>().Count(hatch => hatch.Layer.Name == "MATERIAL-FILTER") == 1
            && entities.OfType<Hatch>().Count(hatch => hatch.Layer.Name == "NATIVE-KEEP") == 1,
            "Hatches with the same envelope but different full boundaries are never confused");

        CadDocument duplicateNative = DwgRegression.Sheet(), duplicateFiltered = DwgRegression.Sheet();
        var duplicateLayer = new Layer("NATIVE-KEEP"); duplicateNative.Layers.Add(duplicateLayer);
        var duplicateMarker = new Layer("REVIT-FILTER"); duplicateFiltered.Layers.Add(duplicateMarker);
        var duplicate = (Hatch)linkedTriangle.Clone(); duplicate.Layer = duplicateLayer;
        duplicateNative.Entities.Add(duplicate); duplicateNative.Entities.Add((Hatch)duplicate.Clone());
        var singleMarker = (Hatch)linkedTriangle.Clone(); singleMarker.Layer = duplicateMarker;
        singleMarker.Color = new ACadSharp.Color(201); duplicateFiltered.Entities.Add(singleMarker);
        string duplicateNativePath = Path.Combine(output, "nge-duplicate-native.dwg");
        string duplicateFilteredPath = Path.Combine(output, "nge-duplicate-filtered.dwg");
        string duplicateResultPath = Path.Combine(output, "nge-duplicate-result.dwg");
        DwgWriter.Write(duplicateNativePath, duplicateNative); DwgWriter.Write(duplicateFilteredPath, duplicateFiltered);
        try
        {
            new ManagedDwgProcessor().Run(new BridgeRequest
            {
                Operation = "Flatten", OutputPath = duplicateResultPath, FilterReferencePath = duplicateFilteredPath,
                ColorRemaps = new() { new() { MarkerAci = 201, Layer = "MATERIAL-FILTER", Color = 4,
                    RuleId = "material", RemapFills = true, SourceLayers = new() { "NATIVE-KEEP" } } }
            }, duplicateNativePath, output);
            check(false, "Ambiguous duplicate hatch classification must fail");
        }
        catch (InvalidDataException ex)
        {
            check(ex.Message.Contains("해치 1:1"), "Duplicate hatch failure explains strict one-to-one protection");
            check(!File.Exists(duplicateResultPath), "Failed hatch matching never publishes a partial DWG");
        }

        CadDocument sharedNative = DwgRegression.Sheet(), sharedFiltered = DwgRegression.Sheet();
        var sharedLayer = new Layer("NATIVE-KEEP"); sharedNative.Layers.Add(sharedLayer);
        var sharedMarkerLayer = new Layer("REVIT-FILTER"); sharedFiltered.Layers.Add(sharedMarkerLayer);
        var sharedBlock = new BlockRecord("SHARED-HATCH");
        var sharedHatch = (Hatch)linkedTriangle.Clone(); sharedHatch.Layer = sharedLayer;
        // Use local coordinates so the two block occurrences have different world signatures.
        sharedHatch.Paths.Clear(); sharedHatch.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
        {
            new Hatch.BoundaryPath.Polyline(new[] { XYZ.Zero, new XYZ(50, 50, 0), new XYZ(100, 0, 0) })
        }));
        sharedBlock.Entities.Add(sharedHatch);
        sharedNative.Entities.Add(new Insert(sharedBlock));
        sharedNative.Entities.Add(new Insert(sharedBlock) { InsertPoint = new XYZ(200, 0, 0) });
        var oneOccurrence = (Hatch)sharedHatch.Clone(); oneOccurrence.Layer = sharedMarkerLayer;
        oneOccurrence.Color = new ACadSharp.Color(201); sharedFiltered.Entities.Add(oneOccurrence);
        string sharedNativePath = Path.Combine(output, "nge-shared-native.dwg");
        string sharedFilteredPath = Path.Combine(output, "nge-shared-filtered.dwg");
        string sharedResultPath = Path.Combine(output, "nge-shared-result.dwg");
        DwgWriter.Write(sharedNativePath, sharedNative); DwgWriter.Write(sharedFilteredPath, sharedFiltered);
        try
        {
            new ManagedDwgProcessor().Run(new BridgeRequest
            {
                Operation = "Flatten", OutputPath = sharedResultPath, FilterReferencePath = sharedFilteredPath,
                ColorRemaps = new() { new() { MarkerAci = 201, Layer = "MATERIAL-FILTER", Color = 4,
                    RuleId = "material", RemapFills = true, SourceLayers = new() { "NATIVE-KEEP" } } }
            }, sharedNativePath, output);
            check(false, "Partially matched shared hatch definition must fail");
        }
        catch (InvalidDataException ex)
        {
            check(ex.Message.Contains("공유 블록 정의"), "Shared hatch occurrence failure identifies unsafe partial relayering");
            check(!File.Exists(sharedResultPath), "Shared-block hatch ambiguity never publishes a partial DWG");
        }
    }
}
