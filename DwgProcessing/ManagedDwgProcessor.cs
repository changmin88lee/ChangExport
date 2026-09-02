using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using System.Diagnostics;

namespace ChangExport.DwgProcessing;

/// <summary>Managed, in-process DWG processing. Never starts or probes a CAD application.</summary>
public sealed partial class ManagedDwgProcessor
{
    public const string EngineName = "내장 DWG 엔진 (ACadSharp 3.7.1)";
    private const double Epsilon = 1e-8;

    public BridgeResponse Run(BridgeRequest request, string inputDrawing, string workingDirectory,
        Func<bool>? cancel = null, Action? pump = null)
    {
        Action Check = CreateCheck(cancel, pump);
        Check();
        if (File.Exists(request.OutputPath)) throw new IOException("기존 DWG는 덮어쓰지 않습니다: " + request.OutputPath);
        if (request.Operation is not ("Flatten" or "Merge")) throw new ArgumentException("지원하지 않는 DWG 작업입니다.");
        var response = new BridgeResponse { OutputPath = request.OutputPath };
        CadDocument document;
        GeometryContext? geometry = null;
        if (request.Operation == "Flatten")
        {
            var prepared = Prepare(request, inputDrawing, cancel, pump);
            return SavePrepared(prepared.Document, prepared.Response, request.OutputPath, workingDirectory, Check);
        }
        else
        {
            geometry = new GeometryContext(request);
            document = Merge(request, response, Check);
        }
        if (request.RevitSheet) document = EditableModel(document, response, Check, geometry: geometry);
        ApplyLayerStyles(document, request.LayerStyles);
        if (request.UseLayerColors) NormalizeLayerColors(document, response, Check, geometry);
        return SavePrepared(document, response, request.OutputPath, workingDirectory, Check);
    }

    public PreparedDrawing Prepare(BridgeRequest request, string inputDrawing, Func<bool>? cancel = null, Action? pump = null)
    {
        Action Check = CreateCheck(cancel, pump);
        Check();
        var clock = Stopwatch.StartNew();
        var response = new BridgeResponse { OutputPath = request.OutputPath };
        var phase = Stopwatch.StartNew();
        CadDocument source = Read(inputDrawing, response.Warnings);
        var geometry = new GeometryContext(request);
        TagFamilyBlocks(source, geometry);
        response.TimingsMs["read"] = phase.Elapsed.TotalMilliseconds; phase.Restart();
        var referenceLayers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        response.FilterContainerMarkersIgnored = BindReferences(source, inputDrawing, response.Warnings, Check,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), referenceLayers, request.ColorRemaps, d => TagFamilyBlocks(d, geometry));
        if (response.FilterContainerMarkersIgnored > 0)
            response.Warnings.Add($"재료/유형 필터 보호: 배치 뷰 외부참조 식별색 {response.FilterContainerMarkersIgnored:N0}개를 전체 뷰에 전파하지 않았습니다.");
        RemoveExcludedGeometry(source, request.ExcludedLayers, response);
        response.TimingsMs["bindReferences"] = phase.Elapsed.TotalMilliseconds; phase.Restart();
        if (request.RevitSheet)
        {
            PrepareRevitSheet(source, response);
            response.ModelScale = RevitModelScale(source, response.Warnings);
        }
        CadDocument document = Flatten(source, response.Warnings, Check, request.RevitSheet);
        RestoreReferenceLayerNames(document, referenceLayers, response.Warnings);
        document = ApplyCustomRemaps(document, request, response, geometry);
        RestoreWideLayers(document, geometry);
        response.TimingsMs["flattenAndLayers"] = phase.Elapsed.TotalMilliseconds; phase.Restart();
        if (request.RevitSheet) document = EditableModel(document, response, Check, geometry: geometry);
        ApplyLayerStyles(document, request.LayerStyles.Concat(referenceLayers.SelectMany(pair => request.LayerStyles
            .Where(s => RevitDwgLayerNames.Equivalent(s.Layer, pair.Value))
            .Select(s => new LayerAppearance { Layer = pair.Key, Color = s.Color, Linetype = s.Linetype, Lineweight = s.Lineweight }))));
        // Marker colors must be consumed by ApplyCustomRemaps before removing overrides.
        if (request.UseLayerColors) NormalizeLayerColors(document, response, Check, geometry);
        DeduplicateFamilies(document, response, geometry);
        response.FamilyBlockMatches = geometry.FamilyMatches;
        int unmatchedFamilies = geometry.FamilyMatches.Count(m => m.Status.EndsWith("개별 객체 유지", StringComparison.Ordinal));
        if (unmatchedFamilies > 0)
            response.Warnings.Add($"패밀리 연결 확인: {unmatchedFamilies:N0}개 정의를 개별 객체로 유지했습니다. 진단 로그의 FamilyBlockMatches를 확인하세요.");
        if (request.WideLineLayers.Count > 0)
            response.Warnings.Add($"전역폭: 변환 {response.WideLineConverted:N0}개 · 원본 유지 {response.WideLineSkipped:N0}개 · Revit DWG 원본 선굵기 × 시트 배율 {response.ModelScale:G}");
        response.TimingsMs["editableObjects"] = phase.Elapsed.TotalMilliseconds;
        Check();
        response.ModelEntityCount = document.Entities.Count;
        response.TimingsMs["prepare"] = clock.Elapsed.TotalMilliseconds;
        return new PreparedDrawing(document, response, inputDrawing);
    }

    private static Action CreateCheck(Func<bool>? cancel, Action? pump)
    {
        var clock = Stopwatch.StartNew();
        return () =>
        {
            if (pump != null && clock.ElapsedMilliseconds >= 100) { pump(); clock.Restart(); }
            if (cancel?.Invoke() == true) throw new OperationCanceledException();
        };
    }

    private static BridgeResponse SavePrepared(CadDocument document, BridgeResponse response, string outputPath, string workingDirectory, Action Check)
    {
        if (File.Exists(outputPath)) throw new IOException("기존 DWG는 덮어쓰지 않습니다: " + outputPath);
        response.OutputPath = outputPath;
        var clock = Stopwatch.StartNew();
        Check();
        Directory.CreateDirectory(workingDirectory);
        string temporary = Path.Combine(workingDirectory, "managed_" + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            var issues = new List<string>();
            DwgWriter.Write(temporary, document, notification: (_, e) =>
            {
                if (e.NotificationType != NotificationType.None) issues.Add(e.Message);
            });
            if (issues.Count > 0) throw new InvalidDataException("DWG 저장 중 누락 가능성이 발견되어 중단했습니다: " + string.Join(" / ", issues));
            Check();
            CadDocument reopened = Read(temporary, response.Warnings);
            VerifyRoundTrip(document, reopened);
            response.ModelEntityCount = reopened.Entities.Count;
            response.PaperEntityCount = reopened.Layouts.Where(l => l.IsPaperSpace)
                .Sum(l => l.AssociatedBlock.Entities.Count(e => e is not Viewport));
            if (response.ModelEntityCount == 0 || response.PaperEntityCount != 0)
                throw new InvalidDataException("모형공간 출력 검사를 통과하지 못했습니다.");
            response.EntityBounds = reopened.Entities.Select(e => ToPlacement(Bounds(e), e.Handle.ToString())).ToList();
            Check();
            File.Move(temporary, outputPath, false);
            response.TimingsMs["writeAndVerify"] = clock.Elapsed.TotalMilliseconds;
            response.Success = true;
            response.Message = "내장 엔진 모형공간 변환 및 DWG 재열기 검사 완료";
            return response;
        }
        catch (InvalidDataException ex)
        {
            string diagnostic = string.Empty;
            if (File.Exists(temporary))
            {
                diagnostic = Path.Combine(workingDirectory, "diagnostic_failed_" + Path.GetFileName(temporary));
                try { File.Move(temporary, diagnostic, false); }
                catch { diagnostic = string.Empty; }
            }
            string message = ex.Message + (diagnostic.Length == 0 ? string.Empty
                : Environment.NewLine + "실패한 재열기 진단 DWG: " + diagnostic);
            throw new InvalidDataException(message, ex);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static CadDocument Read(string path, List<string> warnings)
    {
        var errors = new List<string>();
        var doc = DwgReader.Read(path, new DwgReaderConfiguration { Failsafe = false, KeepUnknownEntities = true }, (_, e) =>
        {
            if (e.NotificationType == NotificationType.None) return;
            // Unknown non-graphical metadata (e.g. unused CAD detail-view styles) is
            // reported. Graphical entities are checked only when included in output:
            // contents belonging solely to an omitted viewport must not stop a sheet.
            if (e.NotificationType is NotificationType.Error or NotificationType.NotImplemented or NotificationType.NotSupported)
                errors.Add(e.Message);
            else if (e.Message.Contains("UnknownNonGraphicalObject", StringComparison.Ordinal)
                || e.Message.Contains("dictionary ACAD_DETAILVIEWSTYLE", StringComparison.Ordinal)
                || e.Message.Contains("dictionary ACAD_SECTIONVIEWSTYLE", StringComparison.Ordinal)
                || e.Message.Contains("ACadSharp.Objects.TableStyle+CellStyle", StringComparison.Ordinal)
                || IsRevitMetadataWarning(e.Message)) warnings.Add(e.Message);
            else errors.Add(e.Message);
        });
        if (errors.Count > 0) throw new InvalidDataException("DWG 읽기 실패 (" + path + "): " + string.Join(" / ", errors));
        if (doc.BlockRecords.SelectMany(b => b.Entities).Any(e => e is TableEntity)
            && warnings.Any(w => w.Contains("TableStyle+CellStyle", StringComparison.Ordinal)))
            throw new InvalidDataException("CAD 표의 문자 스타일을 읽을 수 없어 출력을 중단했습니다.");
        // AC1021 has no writer; pre-Unicode writers do not reliably preserve the
        // Korean code-page header. Never silently downgrade/replace such output.
        if (doc.Header.Version < ACadVersion.AC1024)
            throw new NotSupportedException("내장 엔진은 한글 보존을 위해 DWG 2010 이상만 출력합니다. Revit 출력 설정에서 DWG 2010 이상을 선택하세요.");
        foreach (BlockRecord block in doc.BlockRecords)
        {
            foreach (Entity entity in block.Entities)
            {
                SnapshotTextField(entity, warnings);
                if (entity is Insert insert) foreach (var attribute in insert.Attributes) SnapshotTextField(attribute, warnings);
            }
        }
        var checkedBlocks = new HashSet<BlockRecord>();
        foreach (BlockRecord block in doc.BlockRecords) CheckBlockGraph(block, new HashSet<BlockRecord>(), checkedBlocks);
        if (doc.Layers.Any(l => l.XDictionary?.EntryNames.Any(n => n.Contains("_OVR", StringComparison.OrdinalIgnoreCase)) == true))
            throw new NotSupportedException("뷰포트별 레이어 속성 재지정이 있는 DWG는 현재 변환하지 않습니다.");
        return doc;
    }

    private static void CheckBlockGraph(BlockRecord block, HashSet<BlockRecord> path, HashSet<BlockRecord> done)
    {
        if (done.Contains(block)) return;
        if (path.Count > 64 || !path.Add(block)) throw new InvalidDataException("순환 참조 또는 지나치게 깊은 CAD 블록입니다.");
        foreach (Entity e in block.Entities)
        {
            if (e is Insert i) CheckBlockGraph(i.Block, path, done);
            else if (e is Dimension d && d.Block != null) CheckBlockGraph(d.Block, path, done);
        }
        path.Remove(block); done.Add(block);
    }

    private static void SnapshotTextField(Entity entity, List<string> warnings)
    {
        if (entity.XDictionary?.ContainsKey("ACAD_FIELD") != true) return;
        string? value = entity is MText m ? m.Value : entity is TextEntity text ? text.Value : null;
        if (value == null || value.Contains("%<", StringComparison.Ordinal))
            throw new NotSupportedException("현재 표시 문자열을 확인할 수 없는 CAD 필드가 있습니다.");
        // Export is a snapshot: keep the displayed string, not a live reference to
        // a layout/viewport that will no longer exist in the model-space output.
        entity.XDictionary.Remove("ACAD_FIELD");
        const string warning = "CAD 문자 필드는 현재 표시 문자열로 고정했습니다. 원본 DWG는 변경하지 않았습니다.";
        if (!warnings.Contains(warning)) warnings.Add(warning);
    }

    private static void ValidateEntity(Entity e)
    {
        if (e is UnknownEntity or ProxyEntity or Solid3D or CadBody or ACadSharp.Entities.Region or PdfUnderlay)
            throw new NotSupportedException($"내장 엔진에서 손실 없는 변환이 확인되지 않은 객체입니다: {e.ObjectName} (레이어 {e.Layer.Name}). 이 세트는 저장하지 않습니다.");
        if (e is Insert i && (i.IsMultiple || i.Block.IsDynamic))
            throw new NotSupportedException("동적/배열 블록은 현재 내장 엔진 변환 대상이 아닙니다: " + i.Block.Name);
    }

    private static CadDocument CreateOutput(CadDocument source)
    {
        var target = new CadDocument(source.Header.Version);
        target.Header.LineTypeScale = source.Header.LineTypeScale;
        target.Header.CodePage = source.Header.CodePage;
        target.Header.MeasurementUnits = source.Header.MeasurementUnits;
        target.Header.InsUnits = UnitsType.Millimeters;
        target.Header.ShowModelSpace = true;
        target.Header.ExternalReferenceClippingBoundaryType = XClipFrameType.None;
        target.ModelSpace.Units = UnitsType.Millimeters;
        CopyLayers(source, target, true);
        return target;
    }

    private static void CopyLayers(CadDocument source, CadDocument target, bool first)
    {
        foreach (LineType type in source.LineTypes)
        {
            if (!target.LineTypes.TryGetValue(type.Name, out LineType existing)) target.LineTypes.Add((LineType)type.Clone());
            else if (!type.Segments.Select(SegmentSignature).SequenceEqual(existing.Segments.Select(SegmentSignature)))
                throw new InvalidDataException("서로 다른 정의를 가진 선종류가 있습니다: " + type.Name);
        }
        foreach (Layer layer in source.Layers)
        {
            if (!target.Layers.TryGetValue(layer.Name, out Layer existing)) target.Layers.Add((Layer)layer.Clone());
            else if (first)
            {
                existing.Color = layer.Color; existing.LineWeight = layer.LineWeight;
                existing.LineType = target.LineTypes[layer.LineType.Name]; existing.Flags = layer.Flags;
                existing.IsOn = layer.IsOn; existing.PlotFlag = layer.PlotFlag;
            }
            else if (!existing.Color.Equals(layer.Color) || existing.LineWeight != layer.LineWeight || existing.LineType.Name != layer.LineType.Name
                || existing.IsOn != layer.IsOn || existing.Flags != layer.Flags || existing.PlotFlag != layer.PlotFlag)
                throw new InvalidDataException("시트 사이에 같은 이름의 서로 다른 레이어 설정이 있습니다: " + layer.Name);
        }
    }

    private static object SegmentSignature(LineType.Segment s) =>
        (s.Length, s.Flags, s.Offset, s.Rotation, s.Scale, s.ShapeNumber, s.Text, s.Style?.Filename, s.Style?.BigFontFilename);

    private static CadDocument Flatten(CadDocument source, List<string> warnings, Action check, bool revitSheet = false)
    {
        var layouts = source.Layouts.Where(l => l.IsPaperSpace && l.AssociatedBlock.Entities.Any(e => e is not Viewport || e is Viewport v && !v.RepresentsPaper)).ToList();
        if (layouts.Count != 1) throw new InvalidDataException("시트 DWG에는 내용이 있는 배치가 정확히 하나 있어야 합니다.");
        Layout layout = layouts[0];
        double unitScale = layout.PaperUnits switch
        {
            PlotPaperUnits.Millimeters => 1,
            PlotPaperUnits.Inches => 25.4,
            _ => throw new NotSupportedException("픽셀 단위 시트는 변환할 수 없습니다.")
        };
        CadDocument target = CreateOutput(source);
        var sheet = new BlockRecord("CE_SHEET") { Units = UnitsType.Millimeters };
        var sheetOrder = new List<Entity>();
        var omitted = new List<Viewport>();
        bool Active(Viewport v) => revitSheet ? IsEnabledViewport(v) : IsActiveView(v);
        bool hasOmittedViews = layout.AssociatedBlock.Entities.OfType<Viewport>().Any(v => Active(v) && OmitViewport(v));
        int index = 0;
        var lineworkBounds = new Dictionary<Entity, Box?>();
        foreach (Entity entity in layout.AssociatedBlock.GetSortedEntities())
        {
            check();
            if (entity is Viewport viewport)
            {
                if (!Active(viewport)) continue;
                if (OmitViewport(viewport))
                {
                    omitted.Add(viewport);
                    warnings.Add($"생략: 원근·음영 뷰포트 {viewport.Handle:X}. 도곽·주석과 다른 뷰포트의 출력은 계속했습니다.");
                    continue;
                }
                var converted = ConvertViewport(source, viewport, "CE_VIEW_" + ++index, warnings, check, hasOmittedViews, revitSheet ? lineworkBounds : null);
                sheet.Entities.Add(converted); sheetOrder.Add(converted);
            }
            else
            {
                Entity clone = (Entity)entity.Clone();
                clone = PrepareClone(clone, "CE_PAPER_", new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1, new HashSet<BlockRecord>(), warnings);
                sheet.Entities.Add(clone); sheetOrder.Add(clone);
            }
        }
        if (!sheet.Entities.Any(e => !e.IsInvisible && e.Layer.IsOn && !e.Layer.Flags.HasFlag(LayerFlags.Frozen)) && omitted.Count > 0)
        {
            // Keep an otherwise empty sheet in the set instead of losing its slot.
            double left = omitted.Min(v => v.Center.X - v.Width / 2), right = omitted.Max(v => v.Center.X + v.Width / 2);
            double bottom = omitted.Min(v => v.Center.Y - v.Height / 2), top = omitted.Max(v => v.Center.Y + v.Height / 2);
            var outline = new LwPolyline(new[] { new XY(left, bottom), new XY(right, bottom), new XY(right, top), new XY(left, top) }
                .Select(p => new LwPolyline.Vertex(p))) { IsClosed = true };
            sheet.Entities.Add(outline); sheetOrder.Add(outline);
            warnings.Add("대체: 원근·음영 뷰만 있는 시트는 해당 뷰의 범위 사각형으로 세트 내 자리를 유지했습니다.");
        }
        if (sheet.Entities.Count == 0) throw new InvalidDataException("출력할 시트 내용이 없습니다.");
        PreserveMaskDrawOrder(sheet, sheetOrder);
        target.Entities.Add(new Insert(sheet) { XScale = unitScale, YScale = unitScale, ZScale = unitScale });
        SetExtents(target);
        return target;
    }

    private static bool IsActiveView(Viewport viewport) =>
        !viewport.RepresentsPaper && viewport.ActiveStatus != 0 && !viewport.Status.HasFlag(ViewportStatusFlags.ViewportOff);

    private static bool OmitViewport(Viewport viewport) =>
        (viewport.Status & (ViewportStatusFlags.PerspectiveMode | ViewportStatusFlags.HidePlotMode)) != 0
        || viewport.RenderMode is not (RenderMode.Optimized2D or RenderMode.Wireframe)
        || viewport.ShadePlotMode is ShadePlotMode.Hidden or ShadePlotMode.Rendered;

    private static Insert ConvertViewport(CadDocument source, Viewport viewport, string prefix, List<string> warnings, Action check, bool hasOmittedViews, Dictionary<Entity, Box?>? lineworkBounds = null)
    {
        if (!double.IsFinite(viewport.ScaleFactor) || viewport.ScaleFactor <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
            throw new InvalidDataException("뷰포트 크기 또는 축척이 올바르지 않습니다.");
        if (Math.Abs(viewport.ViewDirection.X) > Epsilon || Math.Abs(viewport.ViewDirection.Y) > Epsilon || viewport.ViewDirection.Z <= 0
            || (viewport.Status & (ViewportStatusFlags.FrontClipping | ViewportStatusFlags.BackClipping)) != 0)
            throw new NotSupportedException("3차원 방향 또는 앞/뒤 잘림 뷰포트는 현재 내장 엔진에서 변환하지 않습니다. 해당 시트의 2D 출력 설정을 확인하세요.");
        var frozen = viewport.FrozenLayers.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var block = new BlockRecord(prefix);
        // PSLTSCALE=1 keeps dash lengths in paper units despite the viewport scale.
        double lineScale = source.Header.PaperSpaceLineTypeScaling == SpaceLineTypeScaling.Normal ? 1 / viewport.ScaleFactor : 1;
        var ordered = new List<Entity>();
        foreach (Entity entity in source.ModelSpace.GetSortedEntities())
        {
            check();
            if (frozen.Contains(entity.Layer.Name)) continue;
            if (OmitModelGeometry(entity, hasOmittedViews, warnings)) continue;
            if (lineworkBounds != null && OutsideViewportLinework(entity, viewport, lineworkBounds)) continue;
            Entity clone = (Entity)entity.Clone();
            clone = PrepareClone(clone, prefix + "_", frozen, lineScale, new HashSet<BlockRecord>(), warnings, hasOmittedViews);
            block.Entities.Add(clone);
            ordered.Add(clone);
        }
        PreserveMaskDrawOrder(block, ordered);
        double angle = -viewport.TwistAngle;
        double scale = viewport.ScaleFactor;
        XYZ target = Rotate(viewport.ViewTarget, angle);
        var insert = new Insert(block)
        {
            Rotation = angle, XScale = scale, YScale = scale, ZScale = scale,
            InsertPoint = new XYZ(viewport.Center.X - scale * (viewport.ViewCenter.X + target.X),
                viewport.Center.Y - scale * (viewport.ViewCenter.Y + target.Y), -scale * viewport.ViewTarget.Z)
        };
        List<XY> paperBoundary;
        if (viewport.Status.HasFlag(ViewportStatusFlags.NonRectangularClipping))
        {
            if (viewport.Boundary is not LwPolyline poly || !poly.IsClosed || poly.Vertices.Any(v => Math.Abs(v.Bulge) > Epsilon))
                throw new NotSupportedException("곡선 또는 해석할 수 없는 뷰포트 잘림 경계입니다. 이 시트는 저장하지 않습니다.");
            paperBoundary = poly.Vertices.Select(v => v.Location).ToList();
        }
        else paperBoundary = new List<XY>
        {
            new(viewport.Center.X - viewport.Width / 2, viewport.Center.Y - viewport.Height / 2),
            new(viewport.Center.X + viewport.Width / 2, viewport.Center.Y - viewport.Height / 2),
            new(viewport.Center.X + viewport.Width / 2, viewport.Center.Y + viewport.Height / 2),
            new(viewport.Center.X - viewport.Width / 2, viewport.Center.Y + viewport.Height / 2)
        };
        // Store the clipping polygon in the untransformed block's coordinates.
        // The insert then carries both the geometry and its clip into paper space.
        insert.SpatialFilter = new SpatialFilter(SpatialFilter.SpatialFilterEntryName)
        {
            Origin = XYZ.Zero, Normal = XYZ.AxisZ, DisplayBoundary = true,
            BoundaryPoints = paperBoundary.Select(p =>
            {
                XYZ local = Rotate(new XYZ((p.X - insert.InsertPoint.X) / scale, (p.Y - insert.InsertPoint.Y) / scale, 0), -angle);
                return new XY(local.X, local.Y);
            }).ToList()
        };
        return insert;
    }

    private static bool OmitModelGeometry(Entity entity, bool hasOmittedViews, List<string> warnings)
    {
        // Model space is shared by all viewports. A shaded view's ACIS geometry
        // must not leak into the writer while copying a remaining 2D viewport.
        if (!hasOmittedViews || entity is not ModelerGeometry) return false;
        string warning = $"생략: 원근·음영 뷰와 모형공간을 공유하는 3D 표현 객체 {entity.ObjectName} (레이어 {entity.Layer.Name}). 2D 선·문자 출력은 계속했습니다.";
        if (!warnings.Contains(warning)) warnings.Add(warning);
        return true;
    }

    private static Entity PrepareClone(Entity entity, string prefix, HashSet<string> frozen, double lineScale, HashSet<BlockRecord> visited, List<string> warnings, bool hasOmittedViews = false)
    {
        if (entity is RasterImage image)
        {
            // IMAGE U/V vectors describe one pixel in WCS, not the full image.
            // Do not use the SDK image bounds/transform: they ignore these vectors.
            XYZ origin = image.InsertPoint, u = image.UVector * image.Size.X, v = image.VVector * image.Size.Y;
            var rectangle = new Polyline3D(new[] { origin, origin + u, origin + u + v, origin + v }, true);
            rectangle.MatchProperties(image);
            entity = rectangle;
            warnings.Add($"대체: 이미지 '{Path.GetFileName(image.Definition.FileName)}' (레이어 {image.Layer.Name})를 전체 이미지 크기·회전의 사각형으로 표시했습니다. 이미지 파일은 필요하지 않습니다.");
        }
        ValidateEntity(entity);
        entity.LineTypeScale *= lineScale;
        // Rename detached style clones, never the source document's protected
        // Standard entries. This also preserves per-sheet fonts on merge.
        if (entity is MText mtext && mtext.Style != null) mtext.Style.Name = prefix + mtext.Style.Name;
        if (entity is TextEntity text && text.Style != null) text.Style.Name = prefix + text.Style.Name;
        if (entity is Insert attributed)
            foreach (AttributeEntity attribute in attributed.Attributes) PrepareClone(attribute, prefix, frozen, lineScale, visited, warnings, hasOmittedViews);
        if (entity is Dimension dimension)
        {
            dimension.Style.Name = prefix + dimension.Style.Name;
            dimension.Style.Style.Name = prefix + dimension.Style.Style.Name;
        }
        BlockRecord? block = entity is Insert insert ? insert.Block : entity is Dimension dim ? dim.Block : null;
        if (block == null || !visited.Add(block)) return entity;
        block.Name = prefix + block.Name.TrimStart('*');
        block.Flags &= ~BlockTypeFlags.Anonymous;
        var ordered = new List<Entity>();
        bool replaced = false;
        foreach (Entity child in block.GetSortedEntities().ToArray())
        {
            if (frozen.Contains(child.Layer.Name)) { block.Entities.Remove(child); continue; }
            if (OmitModelGeometry(child, hasOmittedViews, warnings)) { block.Entities.Remove(child); continue; }
            Entity prepared = PrepareClone(child, prefix, frozen, lineScale, visited, warnings, hasOmittedViews);
            if (!ReferenceEquals(child, prepared))
            {
                block.Entities.Remove(child); block.Entities.Add(prepared); replaced = true;
            }
            ordered.Add(prepared);
        }
        if (replaced)
        {
            // The SDK collection is a HashSet: removal/addition alone does not
            // preserve draw order. Record it explicitly for image replacements.
            var order = block.CreateSortEntitiesTable(); order.Clear();
            for (int n = 0; n < ordered.Count; n++) order.Add(ordered[n], (ulong)n + 1);
        }
        else PreserveMaskDrawOrder(block, ordered);
        return entity;
    }

    private static CadDocument Merge(BridgeRequest request, BridgeResponse response, Action check, IReadOnlyList<PreparedDrawing>? prepared = null)
    {
        if (request.Inputs.Count == 0) throw new InvalidDataException("세트에 시트가 없습니다.");
        if (request.Direction is not ("Horizontal" or "Vertical") || !double.IsFinite(request.MarginMm) || request.MarginMm < 0)
            throw new InvalidDataException("배치 방향 또는 간격이 올바르지 않습니다.");
        CadDocument? target = null;
        var hatchPatterns = new Dictionary<string, HatchPatternState>(StringComparer.OrdinalIgnoreCase);
        double cursor = 0;
        for (int n = 0; n < request.Inputs.Count; n++)
        {
            check();
            CadDocument source = prepared == null ? Read(request.Inputs[n], response.Warnings) : prepared[n].Document;
            RemoveExcludedGeometry(source, request.ExcludedLayers, response);
            if (request.RevitSheet) IsolateConflictingStyles(source, target, n + 1, hatchPatterns, response.Warnings);
            if (source.Header.InsUnits != UnitsType.Millimeters || source.Layouts.Where(l => l.IsPaperSpace).Any(l => l.AssociatedBlock.Entities.Any(e => e is not Viewport)))
                throw new InvalidDataException("병합 입력은 mm 단위의 모형공간 시트여야 합니다.");
            if (target == null) target = CreateOutput(source);
            else CopyLayers(source, target, false);
            if (target.Header.Version != source.Header.Version || Math.Abs(target.Header.LineTypeScale - source.Header.LineTypeScale) > Epsilon)
                throw new InvalidDataException("서로 다른 DWG 버전/전역 선축척을 가진 시트는 병합할 수 없습니다.");
            var block = new BlockRecord("CE_SET_" + (n + 1)) { Units = UnitsType.Millimeters };
            var ordered = new List<Entity>();
            foreach (Entity entity in source.ModelSpace.GetSortedEntities().ToArray())
            {
                // Clone final leaf objects; removing them individually from an attached
                // DWG document triggers expensive SDK ownership updates.
                Entity clone = (Entity)entity.Clone();
                clone = PrepareClone(clone, "CE_" + (n + 1) + "_", new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1, new HashSet<BlockRecord>(), response.Warnings);
                block.Entities.Add(clone);
                ordered.Add(clone);
            }
            PreserveMaskDrawOrder(block, ordered);
            Box box = Bounds(block.Entities);
            double x = request.Direction == "Horizontal" ? cursor : 0;
            double y = request.Direction == "Vertical" ? -cursor - box.Height : 0;
            target.Entities.Add(new Insert(block) { InsertPoint = new XYZ(x - box.MinX, y - box.MinY, 0) });
            response.Placements.Add(new SheetPlacement { Source = request.Inputs[n], X = x, Y = y, Width = box.Width, Height = box.Height });
            cursor += (request.Direction == "Horizontal" ? box.Width : box.Height) + request.MarginMm;
        }
        SetExtents(target!);
        return target!;
    }

    private static void RemoveExcludedGeometry(CadDocument document, IEnumerable<string> excludedLayers, BridgeResponse response)
    {
        var excluded = excludedLayers.Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excluded.Count == 0) return;
        bool IsExcluded(string name) => excluded.Contains(name) || excluded.Any(marker => name.EndsWith("|" + marker, StringComparison.OrdinalIgnoreCase));
        foreach (BlockRecord block in document.BlockRecords.ToArray())
        {
            var ordered = block.GetSortedEntities().ToArray();
            var retained = ordered.Where(entity => !IsExcluded(entity.Layer.Name)).ToArray();
            if (retained.Length == ordered.Length) continue;
            response.ExcludedEntities += ordered.Length - retained.Length;
            block.Entities.Clear();
            foreach (Entity entity in retained) block.Entities.Add(entity);
            PreserveMaskDrawOrder(block, retained);
        }
    }

    private static void PreserveMaskDrawOrder(BlockRecord block, IReadOnlyList<Entity> ordered)
    {
        if (!ordered.Any(entity => entity is Wipeout)) return;
        var order = block.CreateSortEntitiesTable();
        order.Clear();
        for (int index = 0; index < ordered.Count; index++) order.Add(ordered[index], (ulong)index + 1);
    }

    private static void ApplyLayerStyles(CadDocument target, IEnumerable<LayerAppearance> styles)
    {
        var aliases = target.Layers.GroupBy(layer => RevitDwgLayerNames.Normalize(layer.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        foreach (LayerAppearance edit in styles)
        {
            if (!target!.Layers.TryGetValue(edit.Layer, out Layer? layer)
                && !aliases.TryGetValue(RevitDwgLayerNames.Normalize(edit.Layer), out layer)) continue;
            if (layer == null) continue;
            if (edit.Color.HasValue)
            {
                if (edit.Color.Value is < 1 or > 255) throw new InvalidDataException("잘못된 레이어 색상입니다.");
                layer.Color = new ACadSharp.Color((short)edit.Color.Value);
            }
            if (!string.IsNullOrWhiteSpace(edit.Linetype))
            {
                if (!target.LineTypes.TryGetValue(edit.Linetype, out LineType type)) throw new InvalidOperationException("Linetype missing: " + edit.Linetype);
                layer.LineType = type;
            }
            if (edit.Lineweight.HasValue)
            {
                if (!Enum.IsDefined(typeof(LineWeightType), (short)edit.Lineweight.Value)) throw new InvalidDataException("잘못된 선가중치입니다.");
                layer.LineWeight = (LineWeightType)edit.Lineweight.Value;
            }
        }
    }

    private static string DiagnosticNumber(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string HatchPatternDetails(Hatch hatch)
    {
        if (hatch.Pattern == null) return "없음";
        if (hatch.Pattern.Lines.Count == 0) return $"{hatch.Pattern.Name}: 선 정의 없음";
        return hatch.Pattern.Name + ": " + string.Join(" / ", hatch.Pattern.Lines.Select((line, index) =>
            $"L{index + 1}[각도={DiagnosticNumber(line.Angle)}, 기준=({DiagnosticNumber(line.BasePoint.X)},{DiagnosticNumber(line.BasePoint.Y)}), "
            + $"간격=({DiagnosticNumber(line.Offset.X)},{DiagnosticNumber(line.Offset.Y)}), 점선={string.Join(',', line.DashLengths.Select(DiagnosticNumber))}]"));
    }

    private static string HatchBoundaryDetails(Hatch hatch) => hatch.Paths.Count == 0 ? "없음" : string.Join(" / ",
        hatch.Paths.Select((path, pathIndex) => $"P{pathIndex + 1}[{string.Join(',', path.Edges.Select((edge, edgeIndex) => $"E{edgeIndex + 1}:{edge.Type}"))}]"));

    private static InvalidDataException RoundTripFailure(BlockRecord block, int index, Entity expected, Entity? actual,
        string reason, IEnumerable<string>? details = null)
    {
        var lines = new List<string>
        {
            reason,
            $"블록: {block.Name}",
            $"객체 순번: {index + 1}",
            $"예상 객체: {expected.ObjectName} · Handle {expected.Handle:X} · 레이어 {expected.Layer.Name}",
            actual == null ? "저장 후 객체: 없음" : $"저장 후 객체: {actual.ObjectName} · Handle {actual.Handle:X} · 레이어 {actual.Layer.Name}"
        };
        if (details != null) lines.AddRange(details);
        return new InvalidDataException(string.Join(Environment.NewLine, lines));
    }

    private static void VerifyRoundTrip(CadDocument expected, CadDocument actual)
    {
        if (expected.Header.Version != actual.Header.Version || actual.Header.InsUnits != UnitsType.Millimeters || !actual.Header.ShowModelSpace)
            throw new InvalidDataException("DWG 버전·단위·모형공간 설정이 보존되지 않았습니다.");
        // Check every block, not only top-level INSERT counts: a missing nested
        // text/hatch/entity must not be reported as a successful sheet.
        foreach (BlockRecord before in expected.BlockRecords)
        {
            if (!actual.BlockRecords.TryGetValue(before.Name, out BlockRecord after)) throw new InvalidDataException("DWG 블록이 누락되었습니다: " + before.Name);
            var left = before.GetSortedEntities().ToArray(); var right = after.GetSortedEntities().ToArray();
            if (left.Length != right.Length) throw new InvalidDataException($"DWG 내부 객체 수가 달라졌습니다.{Environment.NewLine}블록: {before.Name}{Environment.NewLine}예상: {left.Length:N0}{Environment.NewLine}저장 후: {right.Length:N0}");
            for (int n = 0; n < left.Length; n++)
            {
                if (left[n].ObjectName != right[n].ObjectName || left[n].Layer.Name != right[n].Layer.Name)
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 객체 종류 또는 레이어가 달라졌습니다.");
                if (!left[n].Color.Equals(right[n].Color) || left[n].LineWeight != right[n].LineWeight
                    || Math.Abs(left[n].LineTypeScale - right[n].LineTypeScale) > Epsilon)
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 객체의 색상·선가중치·선축척이 달라졌습니다.", new[]
                    {
                        $"예상: 색상={left[n].Color}, 선가중치={left[n].LineWeight}, 선축척={DiagnosticNumber(left[n].LineTypeScale)}",
                        $"저장 후: 색상={right[n].Color}, 선가중치={right[n].LineWeight}, 선축척={DiagnosticNumber(right[n].LineTypeScale)}"
                    });
                if (left[n] is Hatch beforeHatch)
                {
                    if (right[n] is not Hatch afterHatch)
                        throw RoundTripFailure(before, n, beforeHatch, right[n], "DWG 해치가 다른 객체 형식으로 저장되었습니다.");
                    var differences = new List<string>();
                    if (beforeHatch.IsSolid != afterHatch.IsSolid) differences.Add($"솔리드: {beforeHatch.IsSolid} → {afterHatch.IsSolid}");
                    if (beforeHatch.Pattern?.Name != afterHatch.Pattern?.Name) differences.Add($"패턴 이름: {beforeHatch.Pattern?.Name ?? "없음"} → {afterHatch.Pattern?.Name ?? "없음"}");
                    // SOLID has no pattern lines. ACadSharp/DWG legitimately normalizes
                    // its unused scale and angle while preserving the visible fill.
                    if (!beforeHatch.IsSolid && Math.Abs(beforeHatch.PatternScale - afterHatch.PatternScale) > Epsilon)
                        differences.Add($"패턴 축척: {DiagnosticNumber(beforeHatch.PatternScale)} → {DiagnosticNumber(afterHatch.PatternScale)}");
                    if (!beforeHatch.IsSolid && (Math.Abs(Math.Sin(beforeHatch.PatternAngle) - Math.Sin(afterHatch.PatternAngle)) > Epsilon
                        || Math.Abs(Math.Cos(beforeHatch.PatternAngle) - Math.Cos(afterHatch.PatternAngle)) > Epsilon)
                    )
                        differences.Add($"패턴 각도(rad): {DiagnosticNumber(beforeHatch.PatternAngle)} → {DiagnosticNumber(afterHatch.PatternAngle)}");
                    if (beforeHatch.Paths.Count != afterHatch.Paths.Count)
                        differences.Add($"경계 경로 수: {beforeHatch.Paths.Count} → {afterHatch.Paths.Count}");
                    int commonPaths = Math.Min(beforeHatch.Paths.Count, afterHatch.Paths.Count);
                    for (int pathIndex = 0; pathIndex < commonPaths; pathIndex++)
                    {
                        var expectedPath = beforeHatch.Paths[pathIndex]; var actualPath = afterHatch.Paths[pathIndex];
                        if (expectedPath.Edges.Count != actualPath.Edges.Count)
                            differences.Add($"경계 P{pathIndex + 1} Edge 수: {expectedPath.Edges.Count} → {actualPath.Edges.Count}");
                        int commonEdges = Math.Min(expectedPath.Edges.Count, actualPath.Edges.Count);
                        for (int edgeIndex = 0; edgeIndex < commonEdges; edgeIndex++)
                            if (expectedPath.Edges[edgeIndex].Type != actualPath.Edges[edgeIndex].Type)
                                differences.Add($"경계 P{pathIndex + 1} E{edgeIndex + 1}: {expectedPath.Edges[edgeIndex].Type} → {actualPath.Edges[edgeIndex].Type}");
                    }
                    if (differences.Count > 0)
                        throw RoundTripFailure(before, n, beforeHatch, afterHatch,
                            "DWG 해치 재열기 검증에서 변경이 발견되었습니다.", differences.Concat(new[]
                            {
                                $"예상 해치: 유형={beforeHatch.PatternType}, 법선={beforeHatch.Normal}, 연관={beforeHatch.IsAssociative}",
                                $"저장 후 해치: 유형={afterHatch.PatternType}, 법선={afterHatch.Normal}, 연관={afterHatch.IsAssociative}",
                                "예상 패턴 정의: " + HatchPatternDetails(beforeHatch),
                                "저장 후 패턴 정의: " + HatchPatternDetails(afterHatch),
                                "예상 경계: " + HatchBoundaryDetails(beforeHatch),
                                "저장 후 경계: " + HatchBoundaryDetails(afterHatch)
                            }));
                }
                if (left[n] is Wipeout beforeMask && (right[n] is not Wipeout afterMask
                    || beforeMask.InsertPoint.DistanceFrom(afterMask.InsertPoint) > Epsilon
                    || beforeMask.UVector.DistanceFrom(afterMask.UVector) > Epsilon || beforeMask.VVector.DistanceFrom(afterMask.VVector) > Epsilon
                    || Math.Abs(beforeMask.Size.X - afterMask.Size.X) > Epsilon || Math.Abs(beforeMask.Size.Y - afterMask.Size.Y) > Epsilon
                    || beforeMask.ShowImage != afterMask.ShowImage || beforeMask.ClippingState != afterMask.ClippingState
                    || beforeMask.ClipType != afterMask.ClipType || beforeMask.ClipMode != afterMask.ClipMode
                    || beforeMask.ClipBoundaryVertices.Count != afterMask.ClipBoundaryVertices.Count
                    || beforeMask.ClipBoundaryVertices.Where((point, pointIndex) =>
                        Math.Abs(point.X - afterMask.ClipBoundaryVertices[pointIndex].X) > Epsilon
                        || Math.Abs(point.Y - afterMask.ClipBoundaryVertices[pointIndex].Y) > Epsilon).Any()))
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 마스킹 영역의 위치·크기·잘림 경계가 달라졌습니다.");
                if (left[n] is Polyline3D outline && (right[n] is not Polyline3D savedOutline
                    || outline.IsClosed != savedOutline.IsClosed || outline.Vertices.Count != savedOutline.Vertices.Count
                    || outline.Vertices.Zip(savedOutline.Vertices).Any(pair => pair.First.Location.DistanceFrom(pair.Second.Location) > Epsilon)))
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 사각형 경계의 좌표 또는 닫힘 상태가 달라졌습니다.");
                if (left[n] is MText text && (right[n] is not MText savedText || text.Value != savedText.Value || text.Style.Filename != savedText.Style.Filename))
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 여러 줄 문자 또는 글꼴이 달라졌습니다.");
                if (left[n] is TextEntity single && (right[n] is not TextEntity savedSingle || single.Value != savedSingle.Value || single.Style.Filename != savedSingle.Style.Filename))
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 문자 또는 글꼴이 달라졌습니다.");
                if (left[n] is Insert beforeInsert && right[n] is Insert afterInsert
                    && (beforeInsert.InsertPoint.DistanceFrom(afterInsert.InsertPoint) > Epsilon
                        || Math.Abs(beforeInsert.XScale - afterInsert.XScale) > Epsilon || Math.Abs(beforeInsert.YScale - afterInsert.YScale) > Epsilon
                        || Math.Abs(Math.Sin(beforeInsert.Rotation) - Math.Sin(afterInsert.Rotation)) > Epsilon
                        || Math.Abs(Math.Cos(beforeInsert.Rotation) - Math.Cos(afterInsert.Rotation)) > Epsilon))
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 블록의 위치·축척·회전이 달라졌습니다.");
                if (left[n] is Insert beforeAttributes && right[n] is Insert afterAttributes
                    && !beforeAttributes.Attributes.Select(a => (a.Tag, a.Value, a.Style.Filename)).SequenceEqual(
                        afterAttributes.Attributes.Select(a => (a.Tag, a.Value, a.Style.Filename))))
                    throw RoundTripFailure(before, n, left[n], right[n], "DWG 블록 속성 문자가 달라졌습니다.");
                if (left[n] is Insert a && a.SpatialFilter is { } clip)
                {
                    if (right[n] is not Insert b || b.SpatialFilter is not { } saved || !saved.DisplayBoundary
                        || clip.BoundaryPoints.Count != saved.BoundaryPoints.Count
                        || clip.BoundaryPoints.Where((p, i) => Math.Abs(p.X - saved.BoundaryPoints[i].X) > Epsilon || Math.Abs(p.Y - saved.BoundaryPoints[i].Y) > Epsilon).Any())
                        throw new InvalidDataException($"DWG 뷰포트 잘림 경계가 보존되지 않았습니다: {before.Name}, 예상 {clip.BoundaryPoints.Count}, 실제 {(right[n] as Insert)?.SpatialFilter?.BoundaryPoints.Count}, 사전 {string.Join(',', right[n].XDictionary?.EntryNames ?? Array.Empty<string>())}.");
                }
            }
        }
    }

    private static XYZ Rotate(XYZ p, double angle) => new(p.X * Math.Cos(angle) - p.Y * Math.Sin(angle), p.X * Math.Sin(angle) + p.Y * Math.Cos(angle), p.Z);

    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }
    private static Box Bounds(IEnumerable<Entity> entities)
    {
        var boxes = entities.Where(e => !e.IsInvisible && e.Layer.IsOn && !e.Layer.Flags.HasFlag(LayerFlags.Frozen)).Select(Bounds).ToList();
        if (boxes.Count == 0) throw new InvalidDataException("시트의 표시 범위를 계산할 수 없습니다.");
        return new Box(boxes.Min(b => b.MinX), boxes.Min(b => b.MinY), boxes.Max(b => b.MaxX), boxes.Max(b => b.MaxY));
    }
    private static Box Bounds(Entity entity)
    {
        if (entity is Insert insert)
        {
            IEnumerable<XYZ> corners;
            if (insert.SpatialFilter is { DisplayBoundary: true } filter)
                corners = filter.BoundaryPoints.Select(p => new XYZ(p.X, p.Y, 0));
            else
            {
                Box b = Bounds(insert.Block.Entities);
                corners = new[] { new XYZ(b.MinX, b.MinY, 0), new XYZ(b.MinX, b.MaxY, 0), new XYZ(b.MaxX, b.MinY, 0), new XYZ(b.MaxX, b.MaxY, 0) };
            }
            // Compute all corners explicitly: the SDK's INSERT bounds use just
            // min/max and do not account for rotations or spatial clipping.
            var points = corners.Select(p =>
            {
                XYZ basePoint = insert.Block.BlockEntity.BasePoint;
                return Rotate(new XYZ((p.X - basePoint.X) * insert.XScale, (p.Y - basePoint.Y) * insert.YScale, 0), insert.Rotation) + insert.InsertPoint;
            }).ToList();
            return new Box(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }
        BoundingBox box = entity.GetBoundingBox();
        if (!double.IsFinite(box.Min.X) || !double.IsFinite(box.Min.Y) || !double.IsFinite(box.Max.X) || !double.IsFinite(box.Max.Y))
            throw new NotSupportedException("객체 범위를 안전하게 계산할 수 없습니다: " + entity.ObjectName);
        return new Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
    }
    private static SheetPlacement ToPlacement(Box box, string source) => new() { Source = source, X = box.MinX, Y = box.MinY, Width = box.Width, Height = box.Height };
    private static void SetExtents(CadDocument document)
    {
        Box box = Bounds(document.Entities);
        document.Header.ModelSpaceExtMin = new XYZ(box.MinX, box.MinY, 0);
        document.Header.ModelSpaceExtMax = new XYZ(box.MaxX, box.MaxY, 0);
    }
}
