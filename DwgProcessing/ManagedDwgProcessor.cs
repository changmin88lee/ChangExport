using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;

namespace ChangExport.DwgProcessing;

/// <summary>Managed, in-process DWG processing. Never starts or probes a CAD application.</summary>
public sealed partial class ManagedDwgProcessor
{
    public const string EngineName = "내장 DWG 엔진 (ACadSharp 3.7.1)";
    private const double Epsilon = 1e-8;

    public BridgeResponse Run(BridgeRequest request, string inputDrawing, string workingDirectory,
        Func<bool>? cancel = null, Action? pump = null)
    {
        void Check() { pump?.Invoke(); if (cancel?.Invoke() == true) throw new OperationCanceledException(); }
        Check();
        if (File.Exists(request.OutputPath)) throw new IOException("기존 DWG는 덮어쓰지 않습니다: " + request.OutputPath);
        if (request.Operation is not ("Flatten" or "Merge")) throw new ArgumentException("지원하지 않는 DWG 작업입니다.");
        var response = new BridgeResponse { OutputPath = request.OutputPath };
        CadDocument document;
        if (request.Operation == "Flatten")
        {
            CadDocument source = Read(inputDrawing, response.Warnings);
            var referenceLayers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            BindReferences(source, inputDrawing, response.Warnings, Check, new HashSet<string>(StringComparer.OrdinalIgnoreCase), referenceLayers);
            if (request.RevitSheet) PrepareRevitSheet(source, response);
            document = Flatten(source, response.Warnings, Check, request.RevitSheet);
            RestoreReferenceLayerNames(document, referenceLayers, response.Warnings);
            document = ApplyCustomRemaps(document, request, response);
            ApplyLayerStyles(document, request.LayerStyles.Concat(referenceLayers.SelectMany(pair => request.LayerStyles
                .Where(s => s.Layer == pair.Value).Select(s => new LayerAppearance { Layer = pair.Key, Linetype = s.Linetype, Lineweight = s.Lineweight }))));
        }
        else document = Merge(request, response, Check);
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
            File.Move(temporary, request.OutputPath, false);
            response.Success = true;
            response.Message = "내장 엔진 모형공간 변환 및 DWG 재열기 검사 완료";
            return response;
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
        var omitted = new List<Viewport>();
        bool Active(Viewport v) => revitSheet ? IsEnabledViewport(v) : IsActiveView(v);
        bool hasOmittedViews = layout.AssociatedBlock.Entities.OfType<Viewport>().Any(v => Active(v) && OmitViewport(v));
        int index = 0;
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
                sheet.Entities.Add(ConvertViewport(source, viewport, "CE_VIEW_" + ++index, warnings, check, hasOmittedViews));
            }
            else
            {
                Entity clone = (Entity)entity.Clone();
                clone = PrepareClone(clone, "CE_PAPER_", new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1, new HashSet<BlockRecord>(), warnings);
                sheet.Entities.Add(clone);
            }
        }
        if (!sheet.Entities.Any(e => !e.IsInvisible && e.Layer.IsOn && !e.Layer.Flags.HasFlag(LayerFlags.Frozen)) && omitted.Count > 0)
        {
            // Keep an otherwise empty sheet in the set instead of losing its slot.
            double left = omitted.Min(v => v.Center.X - v.Width / 2), right = omitted.Max(v => v.Center.X + v.Width / 2);
            double bottom = omitted.Min(v => v.Center.Y - v.Height / 2), top = omitted.Max(v => v.Center.Y + v.Height / 2);
            sheet.Entities.Add(new LwPolyline(new[] { new XY(left, bottom), new XY(right, bottom), new XY(right, top), new XY(left, top) }
                .Select(p => new LwPolyline.Vertex(p))) { IsClosed = true });
            warnings.Add("대체: 원근·음영 뷰만 있는 시트는 해당 뷰의 범위 사각형으로 세트 내 자리를 유지했습니다.");
        }
        if (sheet.Entities.Count == 0) throw new InvalidDataException("출력할 시트 내용이 없습니다.");
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

    private static Insert ConvertViewport(CadDocument source, Viewport viewport, string prefix, List<string> warnings, Action check, bool hasOmittedViews)
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
        foreach (Entity entity in source.ModelSpace.GetSortedEntities())
        {
            check();
            if (frozen.Contains(entity.Layer.Name)) continue;
            if (OmitModelGeometry(entity, hasOmittedViews, warnings)) continue;
            Entity clone = (Entity)entity.Clone();
            clone = PrepareClone(clone, prefix + "_", frozen, lineScale, new HashSet<BlockRecord>(), warnings, hasOmittedViews);
            block.Entities.Add(clone);
        }
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
        return entity;
    }

    private static CadDocument Merge(BridgeRequest request, BridgeResponse response, Action check)
    {
        if (request.Inputs.Count == 0) throw new InvalidDataException("세트에 시트가 없습니다.");
        if (request.Direction is not ("Horizontal" or "Vertical") || !double.IsFinite(request.MarginMm) || request.MarginMm < 0)
            throw new InvalidDataException("배치 방향 또는 간격이 올바르지 않습니다.");
        CadDocument? target = null;
        double cursor = 0;
        for (int n = 0; n < request.Inputs.Count; n++)
        {
            check();
            CadDocument source = Read(request.Inputs[n], response.Warnings);
            if (request.RevitSheet && target != null) IsolateConflictingStyles(source, target, n + 1, response.Warnings);
            if (source.Header.InsUnits != UnitsType.Millimeters || source.Layouts.Where(l => l.IsPaperSpace).Any(l => l.AssociatedBlock.Entities.Any(e => e is not Viewport)))
                throw new InvalidDataException("병합 입력은 mm 단위의 모형공간 시트여야 합니다.");
            if (target == null) target = CreateOutput(source);
            else CopyLayers(source, target, false);
            if (target.Header.Version != source.Header.Version || Math.Abs(target.Header.LineTypeScale - source.Header.LineTypeScale) > Epsilon)
                throw new InvalidDataException("서로 다른 DWG 버전/전역 선축척을 가진 시트는 병합할 수 없습니다.");
            var block = new BlockRecord("CE_SET_" + (n + 1)) { Units = UnitsType.Millimeters };
            foreach (Entity entity in source.ModelSpace.GetSortedEntities())
            {
                Entity clone = (Entity)entity.Clone();
                clone = PrepareClone(clone, "CE_" + (n + 1) + "_", new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1, new HashSet<BlockRecord>(), response.Warnings);
                block.Entities.Add(clone);
            }
            Box box = Bounds(block.Entities);
            double x = request.Direction == "Horizontal" ? cursor : 0;
            double y = request.Direction == "Vertical" ? -cursor - box.Height : 0;
            target.Entities.Add(new Insert(block) { InsertPoint = new XYZ(x - box.MinX, y - box.MinY, 0) });
            response.Placements.Add(new SheetPlacement { Source = request.Inputs[n], X = x, Y = y, Width = box.Width, Height = box.Height });
            cursor += (request.Direction == "Horizontal" ? box.Width : box.Height) + request.MarginMm;
        }
        ApplyLayerStyles(target!, request.LayerStyles);
        SetExtents(target!);
        return target!;
    }

    private static void ApplyLayerStyles(CadDocument target, IEnumerable<LayerAppearance> styles)
    {
        foreach (LayerAppearance edit in styles)
        {
            if (!target!.Layers.TryGetValue(edit.Layer, out Layer layer)) continue;
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
            if (left.Length != right.Length) throw new InvalidDataException("DWG 내부 객체 수가 달라졌습니다: " + before.Name);
            for (int n = 0; n < left.Length; n++)
            {
                if (left[n].ObjectName != right[n].ObjectName || left[n].Layer.Name != right[n].Layer.Name)
                    throw new InvalidDataException("DWG 객체 종류 또는 레이어가 달라졌습니다.");
                if (!left[n].Color.Equals(right[n].Color) || left[n].LineWeight != right[n].LineWeight
                    || Math.Abs(left[n].LineTypeScale - right[n].LineTypeScale) > Epsilon)
                    throw new InvalidDataException("DWG 객체의 색상·선가중치·선축척이 달라졌습니다.");
                if (left[n] is Polyline3D outline && (right[n] is not Polyline3D savedOutline
                    || outline.IsClosed != savedOutline.IsClosed || outline.Vertices.Count != savedOutline.Vertices.Count
                    || outline.Vertices.Zip(savedOutline.Vertices).Any(pair => pair.First.Location.DistanceFrom(pair.Second.Location) > Epsilon)))
                    throw new InvalidDataException("DWG 사각형 경계의 좌표 또는 닫힘 상태가 달라졌습니다.");
                if (left[n] is MText text && (right[n] is not MText savedText || text.Value != savedText.Value || text.Style.Filename != savedText.Style.Filename))
                    throw new InvalidDataException("DWG 여러 줄 문자 또는 글꼴이 달라졌습니다.");
                if (left[n] is TextEntity single && (right[n] is not TextEntity savedSingle || single.Value != savedSingle.Value || single.Style.Filename != savedSingle.Style.Filename))
                    throw new InvalidDataException("DWG 문자 또는 글꼴이 달라졌습니다.");
                if (left[n] is Insert beforeInsert && right[n] is Insert afterInsert
                    && (beforeInsert.InsertPoint.DistanceFrom(afterInsert.InsertPoint) > Epsilon
                        || Math.Abs(beforeInsert.XScale - afterInsert.XScale) > Epsilon || Math.Abs(beforeInsert.YScale - afterInsert.YScale) > Epsilon
                        || Math.Abs(Math.Sin(beforeInsert.Rotation) - Math.Sin(afterInsert.Rotation)) > Epsilon
                        || Math.Abs(Math.Cos(beforeInsert.Rotation) - Math.Cos(afterInsert.Rotation)) > Epsilon))
                    throw new InvalidDataException("DWG 블록의 위치·축척·회전이 달라졌습니다.");
                if (left[n] is Insert beforeAttributes && right[n] is Insert afterAttributes
                    && !beforeAttributes.Attributes.Select(a => (a.Tag, a.Value, a.Style.Filename)).SequenceEqual(
                        afterAttributes.Attributes.Select(a => (a.Tag, a.Value, a.Style.Filename))))
                    throw new InvalidDataException("DWG 블록 속성 문자가 달라졌습니다.");
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
