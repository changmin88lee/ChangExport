using System.Reflection;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ChangExport.DwgProcessing;
using ChangExport.Export;
using ChangExport.Models;
using ChangExport.Standards;
using CSMath;
using Color = ACadSharp.Color;

internal static class GeometryOptionsRegression
{
    internal static void Run(string output, Action<bool, string> check, Action<double, double, string> near, string? realRoot)
    {
        var processor = new ManagedDwgProcessor();
        var frames = new FamilyBlockSource { Identity = "title:T1", Label = "도곽 - T1", IsTitleBlock = true, NativePrefixes = new() { "도곽 - T1-901-" } };
        var column = new FamilyBlockSource { Identity = "column:C1", Label = "기둥 - C1", NativePrefixes = new() { "기둥 - C1-101-" } };
        var column2 = new FamilyBlockSource { Identity = "column:C2", Label = "기둥 - C2", NativePrefixes = new() { "기둥 - C2-102-" } };
        BridgeRequest Request() => new() { RevitSheet = true, UseLayerColors = true,
            FamilySources = new() { frames, column, column2 },
            WideLineLayers = new() { new() { NativeLayer = "CE_TEST_WIDE", TargetLayer = "공통", StyleName = "방수##", DisplayRgb = 0x0CB42C } },
            LayerStyles = new() { new() { Layer = "공통", Color = 7, Lineweight = 211 } } };
        var natives = new List<string>();
        foreach (int scale in new[] { 100, 200 })
        {
            var doc = (CadDocument)typeof(EditableModelRegression).GetMethod("Sheet", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { scale })!;
            var wide = new Layer("CE_TEST_WIDE") { LineWeight = LineWeightType.W50 }; doc.Layers.Add(wide);
            var common = new Layer("공통") { LineWeight = LineWeightType.W50 }; doc.Layers.Add(common);
            doc.Entities.Add(new Line { StartPoint = new XYZ(100, 300, 0), EndPoint = new XYZ(800, 300, 0), Layer = wide, LineWeight = LineWeightType.ByLayer });
            doc.Entities.Add(new Line { StartPoint = new XYZ(100, 400, 0), EndPoint = new XYZ(800, 400, 0), Layer = wide, LineWeight = LineWeightType.W80 });
            doc.Entities.Add(new Line { StartPoint = new XYZ(100, 500, 0), EndPoint = new XYZ(800, 500, 0), Layer = common,
                Color = new Color(80, 180, 230) });
            var translatedPattern = new HatchPattern("CE_TRANSLATED_PATTERN");
            translatedPattern.Lines.Add(new HatchPattern.Line { Angle = 0, BasePoint = XY.Zero,
                Offset = new XY(1905, 1905), DashLengths = new() { 1905, -1905 } });
            var translatedHatch = new Hatch { IsSolid = false, Pattern = translatedPattern,
                PatternType = HatchPatternType.Custom, PatternScale = 1, Color = new Color(255, 0, 255) };
            translatedHatch.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[]
            {
                new Hatch.BoundaryPath.Polyline(new[] { new XYZ(1000, -2000, 0), new XYZ(5000, -2000, 0),
                    new XYZ(5000, 2000, 0), new XYZ(1000, 2000, 0) })
            }));
            doc.Entities.Add(translatedHatch);
            doc.Entities.Add(new Arc { Center = new XYZ(1200, 300, 0), Radius = 100, StartAngle = 0, EndAngle = Math.PI, Layer = wide, LineWeight = LineWeightType.W50 });
            doc.Entities.Add(new Circle { Center = new XYZ(1600, 300, 0), Radius = 100, Layer = wide, LineWeight = LineWeightType.W50 });
            var c1 = new BlockRecord("기둥 - C1-101-평면"); c1.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(200, 0, 0), Layer = common });
            var c2 = new BlockRecord("기둥 - C2-102-평면"); c2.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(300, 0, 0), Layer = common });
            doc.Entities.Add(new Insert(c1) { InsertPoint = new XYZ(1000, 1000, 0) });
            doc.Entities.Add(new Insert(c1) { InsertPoint = new XYZ(2000, 1000, 0) });
            doc.Entities.Add(new Insert(c2) { InsertPoint = new XYZ(3000, 1000, 0) });
            var frame = new BlockRecord("도곽 - T1-901-시트");
            frame.Entities.Add(new Line { StartPoint = new XYZ(3, 3, 0), EndPoint = new XYZ(417, 3, 0) });
            frame.Entities.Add(new TextEntity { Value = "고정 도곽", InsertPoint = new XYZ(5, 5, 0), Height = 2 });
            doc.PaperSpace.Entities.Add(new Insert(frame));
            doc.PaperSpace.Entities.Add(new TextEntity { Value = "시트번호:" + scale, InsertPoint = new XYZ(8, 8, 0), Height = 2 });
            string input = Path.Combine(output, "geometry-" + scale + "-native.dwg"); DwgWriter.Write(input, doc); natives.Add(input);
            var request = Request(); request.Operation = "Flatten"; request.OutputPath = Path.Combine(output, "geometry-" + scale + ".dwg");
            var result = processor.Run(request, input, output); var actual = DwgReader.Read(request.OutputPath);
            check(result.WideLineConverted == 4 && result.WideLineSkipped == 0, "Only marked lines/arcs/circles converted");
            var polys = actual.Entities.OfType<LwPolyline>().Where(e => e.Layer.Name == "공통").ToArray();
            check(polys.Length == 4, "Same output layer does not convert unmarked lines");
            check(polys.Count(p => Math.Abs(p.ConstantWidth - .5 * scale) < 1e-6) == 3, "Original ByLayer weight retained despite requested 2.11 mm layer weight");
            near(polys.Max(p => p.ConstantWidth), .8 * scale, "Individual Revit output weight override used");
            check(polys.All(p => !p.Color.IsByLayer && !p.Color.IsByBlock && p.Color.R == 12 && p.Color.G == 180 && p.Color.B == 44
                && p.LineWeight == LineWeightType.W0), "Only converted ## polylines retain forced Revit RGB without second lineweight");
            check(result.PreservedWideLineColors == 4, "Each successfully converted ## polyline records preserved color");
            check(actual.Entities.OfType<Line>().Any(l => l.Layer.Name == "공통" && l.Color.IsByLayer),
                "Unmarked detail line remains LINE and follows its layer color");
            check(actual.Layers.All(l => !l.Name.Contains("CE_TEST_WIDE")), "No temporary width layer leaks");
            check(result.FamilyBlockReferences == 4, "Two C1, one C2, one titleblock become references");
            check(result.FamilyBlockDefinitions == 3, "C1 instances share one definition, C2 remains separate");
            check(actual.Entities.OfType<Insert>().Select(i => i.Block).GroupBy(b => b.Name).Any(g => g.Count() == 2), "Actual DWG C1 references share a definition");
            check(actual.Entities.OfType<Line>().Any(l => l.Layer.Name == "WALL"), "Wall stays editable line");
            var savedPattern = actual.Entities.OfType<Hatch>().Single(h => h.Pattern?.Name == "CE_TRANSLATED_PATTERN").Pattern!.Lines.Single();
            near(savedPattern.Offset.GetLength(), Math.Sqrt(2) * 1905,
                "Translated viewport keeps hatch repeat offset as a vector");
            near(savedPattern.LineOffset, 1905, "Translated viewport keeps hatch perpendicular repeat spacing");
            check(savedPattern.DashLengths.SequenceEqual(new[] { 1905d, -1905d }),
                "Translated viewport keeps hatch dash lengths at model scale");
            check(actual.Entities.OfType<Dimension>().Any() && actual.Entities.OfType<TextEntity>().Any(t => t.Value == "시트번호:" + scale), "Dimensions and sheet parameter text remain separate");
            // Remove only the test-injected features for baseline checks separately; here
            // flatten the grouped result again and compare with the same native sans grouping.
            var plain = Request(); plain.FamilySources.Clear(); plain.Operation = "Flatten"; plain.OutputPath = Path.Combine(output, "plain-" + scale + ".dwg");
            processor.Run(plain, input, output);
            var explode = new BridgeRequest { Operation = "Merge", RevitSheet = true, UseLayerColors = true,
                Inputs = new() { request.OutputPath }, OutputPath = Path.Combine(output, "exploded-" + scale + ".dwg"),
                WideLineLayers = Request().WideLineLayers };
            processor.Run(explode, request.OutputPath, output);
            PerformanceRegression.Compare(plain.OutputPath, explode.OutputPath, check, normalizePeriodicAngles: true);
        }
        foreach (string direction in new[] { "Horizontal", "Vertical" })
        {
            var prepared = natives.Select(n => processor.Prepare(Request(), n)).ToArray();
            var request = new BridgeRequest { RevitSheet = true, UseLayerColors = true, Direction = direction,
                OutputPath = Path.Combine(output, "geometry-set-" + direction + ".dwg"), WideLineLayers = Request().WideLineLayers };
            var result = processor.MergePrepared(request, prepared, output);
            check(result.FamilyBlockReferences == 8, "Merge keeps family references, not whole sheet blocks");
            var actual = DwgReader.Read(request.OutputPath);
            check(actual.Entities.OfType<LwPolyline>().Any(p => p.ConstantWidth == 50) && actual.Entities.OfType<LwPolyline>().Any(p => p.ConstantWidth == 100), "Merge preserves 100/200 sheet widths without double scaling");
            check(actual.Entities.OfType<LwPolyline>().Where(p => p.ConstantWidth > 0).All(p => p.Color.R == 12 && p.Color.G == 180 && p.Color.B == 44),
                "Prepared ## polyline RGB survives final multi-sheet merge");
            near(result.Placements[0].Width, 42000, "First sheet extents unchanged"); near(result.Placements[1].Width, 84000, "Second sheet extents unchanged");
        }
        string configFile = Path.Combine(output, "geometry-settings.json");
        check(ExportGeometryOptions.CadDisplayRgb(0x000000) == 0xFFFFFF
            && ExportGeometryOptions.CadDisplayRgb(0xFFFFFF) == 0x000000
            && ExportGeometryOptions.CadDisplayRgb(0x12B42C) == 0x12B42C,
            "Revit black/white swap for CAD while chromatic RGB remains unchanged");
        var store = new ExportConfigurationStore(configFile); check(store.Load().WideLineKeyword == "##", "Default keyword is ##");
        var config = new RevitExportConfiguration { WideLineKeyword = "전역폭" }; store.Save(config);
        check(store.Load().WideLineKeyword == "전역폭", "Custom keyword persists"); config.WideLineKeyword = ""; store.Save(config);
        check(store.Load().WideLineKeyword == "", "Empty keyword stays disabled");
        EdgeCases(output, check, near);
        if (realRoot != null) Real(output, realRoot, check);
    }

    private static void EdgeCases(string output, Action<bool, string> check, Action<double, double, string> near)
    {
        var doc = new CadDocument(ACadVersion.AC1024);
        doc.PaperSpace.Layout.PaperUnits = PlotPaperUnits.Millimeters;
        var paper = doc.PaperSpace.Entities.OfType<Viewport>().First();
        paper.Center = new XYZ(6, 4.5, 0); paper.Width = 12; paper.Height = 9; paper.ViewHeight = 12;
        paper.ViewCenter = new XY(6, 4.5); paper.Status = ViewportStatusFlags.CurrentlyAlwaysEnabled | ViewportStatusFlags.UcsIconVisibility; paper.ActiveStatus = 1;
        doc.PaperSpace.Entities.Add(new Viewport { Center = new XYZ(200, 200, 0), Width = 400, Height = 400, ViewHeight = 400,
            ViewCenter = new XY(200, 200), ViewDirection = XYZ.AxisZ, ActiveStatus = 1 });
        var marked = new Layer("MARK") { LineWeight = LineWeightType.W50 }; doc.Layers.Add(marked);
        var good = new BlockRecord("Door - D1-301-Plan");
        good.Entities.Add(new Arc { Radius = 10, StartAngle = 0, EndAngle = Math.PI / 2 });
        good.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(10, 0, 0) });
        doc.Entities.Add(new Insert(good) { InsertPoint = new XYZ(50, 50, 0), Rotation = Math.PI });
        doc.Entities.Add(new Insert(good) { InsertPoint = new XYZ(100, 100, 0), Rotation = Math.PI / 2 });
        var different = new BlockRecord("Door - D1-302-Plan");
        different.Entities.Add(new Line { StartPoint = XYZ.Zero, EndPoint = new XYZ(20, 0, 0) });
        doc.Entities.Add(new Insert(different) { InsertPoint = new XYZ(150, 150, 0) });
        var annotation = new BlockRecord("Detail - D1-900-Plan");
        annotation.Entities.Add(new TextEntity { Value = "독립 주석", Height = 2 });
        doc.Entities.Add(new Insert(annotation) { InsertPoint = new XYZ(200, 200, 0) });
        doc.Entities.Add(new Line { StartPoint = new XYZ(40, 45, 0), EndPoint = new XYZ(70, 45, 0), Layer = marked, Color = new Color(3), LineWeight = LineWeightType.W80 });
        doc.Entities.Add(new Ellipse { Center = new XYZ(250, 250, 0), MajorAxisEndPoint = new XYZ(10, 0, 0), RadiusRatio = .5, Layer = marked });
        doc.Entities.Add(new Line { StartPoint = new XYZ(40, 30, 0), EndPoint = new XYZ(70, 30, 0), Layer = marked, LineWeight = LineWeightType.Default });
        var inherited = new BlockRecord("SharedLine");
        inherited.Entities.Add(new Line { EndPoint = new XYZ(10, 0, 0), LineWeight = LineWeightType.ByBlock });
        doc.Entities.Add(new Insert(inherited) { Layer = marked, LineWeight = LineWeightType.W50, InsertPoint = new XYZ(100, 300, 0) });
        doc.Entities.Add(new Insert(inherited) { Layer = marked, LineWeight = LineWeightType.W80, InsertPoint = new XYZ(150, 300, 0) });
        doc.Entities.Add(new Insert(inherited) { LineWeight = LineWeightType.W80, InsertPoint = new XYZ(200, 300, 0) });
        string input = Path.Combine(output, "geometry-edge-native.dwg"); DwgWriter.Write(input, doc);
        var request = new BridgeRequest { Operation = "Flatten", RevitSheet = true, UseLayerColors = true,
            OutputPath = Path.Combine(output, "geometry-edge.dwg"),
            FamilySources = new() { new() { Identity = "door:D1", Label = "Door D1", NativePrefixes = new() { "Door - D1-301-", "Door - D1-302-" } } },
            WideLineLayers = new() { new() { NativeLayer = "MARK", TargetLayer = "Wide", StyleName = "##" } },
            ColorRemaps = new() { new() { MarkerAci = 3, Color = 7, Layer = "FilterWide", RuleId = "r" } },
            LayerStyles = new() { new() { Layer = "FilterWide", Color = 7, Lineweight = 211 } } };
        var processor = new ManagedDwgProcessor(); var result = processor.Run(request, input, output);
        check(result.Success && result.WideLineConverted == 3 && result.WideLineSkipped == 2, "Unsupported curves and missing widths preserve output, not abort");
        var actual = DwgReader.Read(request.OutputPath);
        near(actual.Entities.OfType<LwPolyline>().Single(p => p.Layer.Name == "FilterWide").ConstantWidth, .8, "Custom filter retains the original weight marker");
        check(actual.Entities.OfType<LwPolyline>().Count(p => p.Layer.Name == "Wide") == 2, "Shared definition inherits width per insertion, unmarked insertion stays a line");
        check(actual.Entities.OfType<LwPolyline>().Where(p => p.Layer.Name == "Wide").Select(p => p.ConstantWidth).Order().SequenceEqual(new[] { .5, .8 }), "ByBlock native widths do not overwrite each other");
        check(actual.Entities.OfType<Ellipse>().Count() == 1, "Unsupported ellipse stays intact");
        check(actual.Entities.OfType<TextEntity>().Any(t => t.Value == "독립 주석"), "Standalone annotation block is exploded");
        check(result.FamilyBlockReferences == 3 && result.FamilyBlockDefinitions >= 2, "Same family/type with different geometry stays separate");
        var baseline = new BridgeRequest { Operation = "Flatten", RevitSheet = true, UseLayerColors = true,
            OutputPath = Path.Combine(output, "geometry-edge-plain.dwg"), WideLineLayers = request.WideLineLayers,
            ColorRemaps = request.ColorRemaps, LayerStyles = request.LayerStyles };
        processor.Run(baseline, input, output);
        string exploded = Path.Combine(output, "geometry-edge-exploded.dwg");
        // The generic merge aligns the origin. Keep the expected baseline aligned too.
        processor.Run(new BridgeRequest { Operation = "Merge", RevitSheet = true, UseLayerColors = true, Inputs = new() { request.OutputPath }, OutputPath = exploded }, request.OutputPath, output);
        string aligned = Path.Combine(output, "geometry-edge-aligned.dwg");
        processor.Run(new BridgeRequest { Operation = "Merge", RevitSheet = true, UseLayerColors = true, Inputs = new() { baseline.OutputPath }, OutputPath = aligned }, baseline.OutputPath, output);
        PerformanceRegression.Compare(aligned, exploded, check, normalizePeriodicAngles: true);
    }

    private static void Real(string output, string root, Action<bool, string> check)
    {
        var hashes = Directory.GetFiles(root, "*.dwg", SearchOption.AllDirectories).ToDictionary(p => p,
            p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
        var processor = new ManagedDwgProcessor();
        var sources = new List<FamilyBlockSource> {
            new() { Identity = "real:title", Label = "LAON_LH 도곽", IsTitleBlock = true,
                NativePrefixes = new() { "LAON_LH_Title Block_A1 - _AN_LAON 실시폼_A3__LH 2-981728-" } },
            new() { Identity = "real:support", Label = "금속 원형 지지", NativePrefixes = new() { "지지 - 금속 - 원형 - 카테고리별-1399680-" } },
            new() { Identity = "real:mullion", Label = "멀리언", NativePrefixes = new() { "직사각형 멀리언 - 50 x 100mm-1281721-" } }
        };
        var paths = Directory.GetFiles(root, "sheet.dwg", SearchOption.AllDirectories);
        var prepared = paths.Select(p => processor.Prepare(new BridgeRequest { RevitSheet = true, UseLayerColors = true, FamilySources = sources }, p)).ToArray();
        var plain = paths.Select(p => processor.Prepare(new BridgeRequest { RevitSheet = true, UseLayerColors = true }, p)).ToArray();
        string grouped = Path.Combine(output, "AA-440-families.dwg"), baseline = Path.Combine(output, "AA-440-baseline.dwg"), exploded = Path.Combine(output, "AA-440-exploded.dwg");
        var result = processor.MergePrepared(new BridgeRequest { RevitSheet = true, UseLayerColors = true, OutputPath = grouped }, prepared, output);
        processor.MergePrepared(new BridgeRequest { RevitSheet = true, UseLayerColors = true, OutputPath = baseline }, plain, output);
        processor.Run(new BridgeRequest { Operation = "Merge", RevitSheet = true, UseLayerColors = true, Inputs = new() { grouped }, OutputPath = exploded }, grouped, output);
        PerformanceRegression.Compare(baseline, exploded, check, normalizePeriodicAngles: true);
        var doc = DwgReader.Read(grouped);
        check(result.FamilyBlockReferences > 2, "Actual Revit DWGs contain grouped model families and titleblocks");
        check(result.FamilyBlockDefinitions < result.FamilyBlockReferences, "Actual repeated Revit content reuses definitions");
        check(hashes.All(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "All original native DWGs unchanged");
        File.WriteAllText(Path.Combine(output, "real-geometry-result.json"), JsonSerializer.Serialize(new { result, bytesGrouped = new FileInfo(grouped).Length,
            bytesBaseline = new FileInfo(baseline).Length }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
