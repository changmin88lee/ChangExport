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
        var nativeTop = new Layer("NATIVE-TOP") { Color = new ACadSharp.Color(7) };
        native.Layers.Add(nativeType); native.Layers.Add(nativeKeep); native.Layers.Add(nativeTop);
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 300, 0), EndPoint = new XYZ(450, 300, 0), Layer = nativeType });
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 305, 0), EndPoint = new XYZ(450, 305, 0), Layer = nativeKeep });
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 320, 0), EndPoint = new XYZ(450, 320, 0), Layer = nativeKeep });
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 340, 0), EndPoint = new XYZ(450, 340, 0), Layer = nativeKeep });
        // A different host object's coincident line is later in the native Revit
        // order and must remain visually above the material line after splitting.
        native.Entities.Add(new Line { StartPoint = new XYZ(300, 340, 0), EndPoint = new XYZ(450, 340, 0), Layer = nativeTop });
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
                    BoundaryPriority = 700, SourceLayers = new() { "NATIVE-KEEP" },
                    DiagnosticSourceCategories = new() { "벽" }, DiagnosticCompoundLayerIndices = new() { 2 },
                    DiagnosticCompoundLayerFunctions = new() { "Finish1" }, DiagnosticMaterialNames = new() { "시멘트 모르타르" },
                    DiagnosticSourceElementCount = 1, DiagnosticSourceElementIds = new() { "TEST|WALL-1" } }
            }
        }, nativePath, output);
        CadDocument saved = DwgReader.Read(resultPath);
        Entity[] entities = DwgRegression.Walk(saved.ModelSpace).ToArray();
        check(response.GeometrySource == "NativeGeometry" && response.NativeOverlayMatchedEntities == 4
            && response.NativeOverlayPartialLinesSplit == 1,
            "NGE transfers exact, fully covered, partially covered and full-boundary hatch classifications");
        var materialDiagnostic = response.NativeOverlayRuleDiagnostics.Single(diagnostic => diagnostic.MarkerAci == 201);
        check(materialDiagnostic.ClassifiedEntities >= 5 && materialDiagnostic.FullLineAssignments == 1
                && materialDiagnostic.PartialLineAssignments == 1 && materialDiagnostic.AppliedEntities == 3
                && materialDiagnostic.UniqueMarkerLineSignatures == 4
                && materialDiagnostic.ExactNativeLineSignatures == 0
                && materialDiagnostic.RejectedNativeLayers.GetValueOrDefault("NATIVE-TOP") >= 1
                && materialDiagnostic.AppliedNativeLayers.GetValueOrDefault("NATIVE-KEEP") == 3
                && materialDiagnostic.CompoundLayerIndices.SequenceEqual(new[] { 2 })
                && materialDiagnostic.SourceElementIds.SequenceEqual(new[] { "TEST|WALL-1" })
                && materialDiagnostic.Samples.Count > 0,
            "NGE diagnostics record compound provenance, source-layer rejection, partial matching and final native origins");
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
        var coincidentOrder = saved.BlockRecords.Select(block => block.GetSortedEntities().OfType<Line>()
                .Where(line => Math.Abs(line.StartPoint.Y - 340) < 1e-8).Select(line => line.Layer.Name).ToArray())
            .FirstOrDefault(order => order.Length > 0) ?? Array.Empty<string>();
        check(coincidentOrder.SequenceEqual(new[] { "MATERIAL-FILTER", "NATIVE-KEEP", "NATIVE-TOP" }),
            "Partial material splitting preserves the native Revit draw order across different host objects: "
                + string.Join(",", coincidentOrder));
        check(entities.OfType<Line>().Any(line => line.Layer.Name == "NATIVE-KEEP"),
            "Nearby native door/floor geometry is not captured by a non-exact material marker");
        check(entities.OfType<Hatch>().Count(hatch => hatch.Layer.Name == "MATERIAL-FILTER") == 1
            && entities.OfType<Hatch>().Count(hatch => hatch.Layer.Name == "NATIVE-KEEP") == 1,
            "Hatches with the same envelope but different full boundaries are never confused");

        CadDocument ownershipNative = DwgRegression.Sheet(), ownershipFiltered = DwgRegression.Sheet();
        Layer finish = new("NATIVE-FINISH"), substrate = new("NATIVE-SUBSTRATE"), generic = new("NATIVE-GENERIC");
        ownershipNative.Layers.Add(finish); ownershipNative.Layers.Add(substrate); ownershipNative.Layers.Add(generic);
        void NativeLine(XYZ start, XYZ end, Layer layer) => ownershipNative.Entities.Add(new Line
            { StartPoint = start, EndPoint = end, Layer = layer });
        NativeLine(new XYZ(0, 0, 0), new XYZ(100, 0, 0), substrate);
        NativeLine(new XYZ(0, 10, 0), new XYZ(100, 10, 0), generic);
        NativeLine(new XYZ(0, 20, 0), new XYZ(100, 20, 0), generic);
        NativeLine(new XYZ(0, 30, 0), new XYZ(100, 30, 0), generic);
        NativeLine(new XYZ(0, 40, 0), new XYZ(100, 40, 0), generic);
        NativeLine(new XYZ(100, 40, 0), new XYZ(100, 80, 0), generic);
        ownershipNative.Entities.Add(new LwPolyline(new[]
        {
            new LwPolyline.Vertex(new XY(0, 90)), new LwPolyline.Vertex(new XY(100, 90)),
            new LwPolyline.Vertex(new XY(100, 120))
        }) { Layer = generic });
        NativeLine(new XYZ(78756.3320401, 100, 0), new XYZ(78756.3320402, 110, 0), generic);
        var ownershipMarker = new Layer("REVIT-FILTER"); ownershipFiltered.Layers.Add(ownershipMarker);
        void MarkerLine(XYZ start, XYZ end, short aci) => ownershipFiltered.Entities.Add(new Line
            { StartPoint = start, EndPoint = end, Layer = ownershipMarker, Color = new ACadSharp.Color(aci) });
        MarkerLine(new XYZ(0, 0, 0), new XYZ(100, 0, 0), 216);
        MarkerLine(new XYZ(0, 10, 0), new XYZ(100, 10, 0), 210);
        MarkerLine(new XYZ(0, 10, 0), new XYZ(100, 10, 0), 211);
        MarkerLine(new XYZ(0, 20, 0), new XYZ(100, 20, 0), 212);
        MarkerLine(new XYZ(0, 20, 0), new XYZ(100, 20, 0), 211);
        MarkerLine(new XYZ(0, 30, 0), new XYZ(100, 30, 0), 210);
        MarkerLine(new XYZ(0, 30, 0), new XYZ(100, 30, 0), 212);
        MarkerLine(new XYZ(0, 30, 0), new XYZ(100, 30, 0), 213);
        ownershipFiltered.Entities.Add(new LwPolyline(new[]
        {
            new LwPolyline.Vertex(new XY(0, 40)), new LwPolyline.Vertex(new XY(100, 40)),
            new LwPolyline.Vertex(new XY(100, 80))
        }) { Layer = ownershipMarker, Color = new ACadSharp.Color(214) });
        MarkerLine(new XYZ(0, 90, 0), new XYZ(100, 90, 0), 217);
        MarkerLine(new XYZ(100, 90, 0), new XYZ(100, 120, 0), 217);
        MarkerLine(new XYZ(78756.3320404, 100, 0), new XYZ(78756.3320398, 110, 0), 215);
        string ownershipNativePath = Path.Combine(output, "nge-ownership-native.dwg");
        string ownershipFilteredPath = Path.Combine(output, "nge-ownership-filtered.dwg");
        string ownershipResultPath = Path.Combine(output, "nge-ownership-result.dwg");
        DwgWriter.Write(ownershipNativePath, ownershipNative); DwgWriter.Write(ownershipFilteredPath, ownershipFiltered);
        BridgeResponse ownershipResponse = new ManagedDwgProcessor().Run(new BridgeRequest
        {
            Operation = "Flatten", OutputPath = ownershipResultPath, FilterReferencePath = ownershipFilteredPath,
            ColorRemaps = new()
            {
                new() { MarkerAci = 210, Layer = "CEMENT", Color = 4, RuleId = "material", RemapFills = true,
                    BoundaryPriority = 1, SourceLayers = new() { "NATIVE-GENERIC" } },
                new() { MarkerAci = 211, Layer = "", Color = 7, RuleId = "__compound_owner__",
                    BoundaryPriority = 2, SourceLayers = new() { "NATIVE-GENERIC" }, PreserveNative = true },
                new() { MarkerAci = 212, Layer = "CEMENT", Color = 4, RuleId = "material", RemapFills = true,
                    BoundaryPriority = 3, SourceLayers = new() { "NATIVE-GENERIC" } },
                new() { MarkerAci = 213, Layer = "TYPE", Color = 3, RuleId = "type", BoundaryPriority = 2,
                    SourceLayers = new() { "NATIVE-GENERIC" } },
                new() { MarkerAci = 214, Layer = "POLY-TYPE", Color = 2, RuleId = "poly" },
                new() { MarkerAci = 215, Layer = "SHORT-TYPE", Color = 1, RuleId = "short" },
                new() { MarkerAci = 216, Layer = "CEMENT", Color = 4, RuleId = "material", RemapFills = true,
                    BoundaryPriority = 1, SourceLayers = new() { "NATIVE-FINISH" } },
                new() { MarkerAci = 217, Layer = "LINE-POLY", Color = 6, RuleId = "line-poly" }
            }
        }, ownershipNativePath, output);
        Entity[] ownershipEntities = DwgRegression.Walk(DwgReader.Read(ownershipResultPath).ModelSpace).ToArray();
        check(ownershipEntities.OfType<Line>().Any(line => line.Layer.Name == "NATIVE-SUBSTRATE"
                && Math.Abs(line.StartPoint.Y) < 1e-8)
            && ownershipEntities.OfType<Line>().Any(line => line.Layer.Name == "NATIVE-GENERIC"
                && Math.Abs(line.StartPoint.Y - 10) < 1e-8),
            "Compound function gates and a higher unfiltered owner preserve the original Native boundary");
        check(ownershipEntities.OfType<Line>().Count(line => line.Layer.Name == "CEMENT") == 2,
            "The actual inner compound index wins and same-target markers retain their greatest priority");
        check(ownershipEntities.OfType<Line>().Count(line => line.Layer.Name == "POLY-TYPE") == 2,
            "Straight segments extracted from an L-shaped marker polyline classify Native lines");
        check(ownershipEntities.OfType<LwPolyline>().Count(polyline => polyline.Layer.Name == "LINE-POLY") == 1,
            "Split marker lines classify a fully owned Native L-shaped polyline");
        check(ownershipEntities.OfType<Line>().Count(line => line.Layer.Name == "SHORT-TYPE") == 1,
            "Endpoint-canonical line keys match short lines at large coordinates");
        check(ownershipResponse.NativeOverlayRuleDiagnostics.Single(diagnostic => diagnostic.MarkerAci == 211).PreservedEntities > 0
            && !ownershipResponse.CustomRuleEntityCounts.ContainsKey("__compound_owner__"),
            "Ownership-only markers report Native preservation without becoming an output rule");

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
