using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Serialization.Json;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using ChangExport.DwgProcessing;

[assembly: CommandClass(typeof(ChangExport.AutoCadBridge.BridgeCommands))]

namespace ChangExport.AutoCadBridge
{
    public sealed class BridgeCommands
    {
        [CommandMethod("CHANGEXPORT", CommandFlags.Modal)]
        public void Execute()
        {
            string responsePath = Environment.GetEnvironmentVariable("CHANGEXPORT_RESPONSE") ?? "";
            var response = new BridgeResponse();
            try
            {
                string requestPath = Environment.GetEnvironmentVariable("CHANGEXPORT_REQUEST") ?? "";
                BridgeRequest request;
                using (var stream = File.OpenRead(requestPath))
                    request = (BridgeRequest)new DataContractJsonSerializer(typeof(BridgeRequest)).ReadObject(stream);
                if (request.Operation == "Palette")
                {
                    for (short index = 1; index <= 255; index++)
                        using (var color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index))
                            response.PaletteRgb.Add(color.ColorValue.ToArgb() & 0xffffff);
                    response.Success = true;
                    return;
                }
                if (File.Exists(request.OutputPath)) throw new IOException("Output already exists: " + request.OutputPath);
                if (request.Operation == "Flatten") Flatten(request, response);
                else if (request.Operation == "Merge") Merge(request, response);
                else if (request.Operation == "Fixture") Fixture(request);
                else throw new InvalidOperationException("Unknown operation.");
                Inspect(request.OutputPath, response);
                if (response.ModelEntityCount == 0) throw new InvalidOperationException("Model space is empty.");
                if (request.Operation != "Fixture" && response.PaperEntityCount != 0)
                    throw new InvalidOperationException("Output still contains paper-space geometry.");
                response.OutputPath = request.OutputPath;
                response.Success = true;
                response.Message = "Model space verified.";
            }
            catch (System.Exception ex) { response.Message = ex.ToString(); }
            finally
            {
                if (!string.IsNullOrEmpty(responsePath))
                    using (var stream = File.Create(responsePath))
                        new DataContractJsonSerializer(typeof(BridgeResponse)).WriteObject(stream, response);
            }
        }

        private static void Flatten(BridgeRequest request, BridgeResponse response)
        {
            Database source = Application.DocumentManager.MdiActiveDocument.Database;
            DwgVersion outputVersion = ReadSaveVersion(source.Filename);
            ObjectId layoutId;
            double scale;
            using (var tr = source.TransactionManager.StartTransaction())
            {
                var layouts = (DBDictionary)tr.GetObject(source.LayoutDictionaryId, OpenMode.ForRead);
                var layoutObjects = new List<Layout>();
                foreach (DBDictionaryEntry entry in layouts)
                    layoutObjects.Add((Layout)tr.GetObject(entry.Value, OpenMode.ForRead));
                var candidates = layoutObjects
                    .Where(l => !l.ModelType)
                    .Where(l => ((BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead))
                        .Cast<ObjectId>().Any(id => !(tr.GetObject(id, OpenMode.ForRead) is Viewport vp) || vp.Number > 1))
                    .ToList();
                if (candidates.Count != 1)
                    throw new InvalidOperationException("Expected exactly one populated sheet layout; found " + candidates.Count + ".");
                Layout layout = candidates[0];
                layoutId = layout.ObjectId;
                if (layout.PlotPaperUnits == PlotPaperUnit.Pixels)
                    throw new InvalidOperationException("Pixel paper units are not supported.");
                scale = layout.PlotPaperUnits == PlotPaperUnit.Inches ? 25.4 : 1.0;
                LayoutManager.Current.CurrentLayout = layout.LayoutName;
                tr.Commit();
            }
            Application.SetSystemVariable("TILEMODE", 0);
            Application.SetSystemVariable("CVPORT", 1);
            var engine = Autodesk.AutoCAD.ExportLayout.Engine.Instance();
            using (Database flattened = engine.ExportLayout(layoutId))
            {
                if (flattened == null) throw new InvalidOperationException("AutoCAD ExportLayout returned no drawing.");
                if (engine.EngineStatus != Autodesk.AutoCAD.ExportLayout.ErrorStatus.Succeeded)
                    throw new InvalidOperationException("AutoCAD ExportLayout: " + engine.EngineStatus);
                using (var tr = flattened.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(flattened.BlockTableId, OpenMode.ForRead);
                    var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in model)
                    {
                        var entity = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                        if (scale != 1) entity.TransformBy(Matrix3d.Scaling(scale, Point3d.Origin));
                    }
                    tr.Commit();
                }
                flattened.Insunits = UnitsValue.Millimeters;
                flattened.TileMode = true;
                BindReferences(flattened);
                flattened.SaveAs(request.OutputPath, outputVersion);
            }
            response.Warnings.Add("EXPORTLAYOUT may change clipped dimensions, blocks, hatch and wipeout representation. Check the DWG against the Revit sheet.");
        }

        private static void Merge(BridgeRequest request, BridgeResponse response)
        {
            if (request.Inputs.Count == 0) throw new InvalidOperationException("No sheets.");
            if (request.Direction != "Horizontal" && request.Direction != "Vertical") throw new InvalidOperationException("Invalid direction.");
            if (double.IsNaN(request.MarginMm) || double.IsInfinity(request.MarginMm) || request.MarginMm < 0) throw new InvalidOperationException("Invalid margin.");
            using (var target = new Database(false, true))
            {
                // Retain the first sheet's exported database settings (including linetype scales/styles)
                // instead of silently replacing them with a blank AutoCAD template's defaults.
                target.ReadDwgFile(request.Inputs[0], FileOpenMode.OpenForReadAndAllShare, true, null);
                target.CloseInput(true);
                DwgVersion version = ReadSaveVersion(request.Inputs[0]);
                using (var tr = target.TransactionManager.StartTransaction())
                {
                    var table = (BlockTable)tr.GetObject(target.BlockTableId, OpenMode.ForRead);
                    var model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in model) tr.GetObject(id, OpenMode.ForWrite).Erase();
                    tr.Commit();
                }
                target.Insunits = UnitsValue.Millimeters;
                target.TileMode = true;
                double cursor = 0;
                foreach (string path in request.Inputs)
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                    source.CloseInput(true);
                    if (source.Insunits != UnitsValue.Millimeters) throw new InvalidOperationException("Expected flattened sheet in millimeters.");
                    source.Insbase = Point3d.Origin;
                    Extents3d bounds = Bounds(source);
                    double width = bounds.MaxPoint.X - bounds.MinPoint.X;
                    double height = bounds.MaxPoint.Y - bounds.MinPoint.Y;
                    double x = request.Direction == "Horizontal" ? cursor : 0;
                    double y = request.Direction == "Vertical" ? -cursor - height : 0;
                    string symbolPrefix = "CHANG_" + Guid.NewGuid().ToString("N") + "_";
                    IsolateNamedBlocks(source, symbolPrefix);
                    ObjectId blockId = target.Insert(symbolPrefix + "SHEET", source, true);
                    using (var tr = target.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(target.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        var block = new BlockReference(new Point3d(x - bounds.MinPoint.X, y - bounds.MinPoint.Y, -bounds.MinPoint.Z), blockId);
                        ms.AppendEntity(block);
                        tr.AddNewlyCreatedDBObject(block, true);
                        tr.Commit();
                    }
                    response.Placements.Add(new SheetPlacement { Source = path, X = x, Y = y, Width = width, Height = height });
                    cursor += (request.Direction == "Horizontal" ? width : height) + request.MarginMm;
                }
                ApplyStyles(target, request);
                BindReferences(target);
                target.UpdateExt(true);
                target.SaveAs(request.OutputPath, version);
            }
        }

        private static void BindReferences(Database db)
        {
            db.ResolveXrefs(false, false);
            var ids = new ObjectIdCollection();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsFromExternalReference) continue;
                    if (btr.XrefStatus != XrefStatus.Resolved) throw new InvalidOperationException("Unresolved Xref: " + btr.Name);
                    ids.Add(id);
                }
                tr.Commit();
            }
            if (ids.Count > 0) db.BindXrefs(ids, true);
        }

        private static DwgVersion ReadSaveVersion(string path)
        {
            // LastSavedAsVersion may expose an internal maintenance version which SaveAs rejects.
            // The on-disk DWG signature identifies the actual supported exchange version.
            var bytes = new byte[6];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                if (stream.Read(bytes, 0, bytes.Length) != bytes.Length) throw new InvalidDataException("Incomplete DWG header.");
            switch (System.Text.Encoding.ASCII.GetString(bytes))
            {
                case "AC1015": return DwgVersion.AC1015;
                case "AC1018": return DwgVersion.AC1800;
                case "AC1021": return DwgVersion.AC1021;
                case "AC1024": return DwgVersion.AC1024;
                case "AC1027": return DwgVersion.AC1027;
                case "AC1032": return DwgVersion.AC1032;
                default: throw new InvalidDataException("Unsupported DWG signature; export version was not changed.");
            }
        }

        private static void IsolateNamedBlocks(Database source, string prefix)
        {
            // INSERT reuses named definitions already in the destination. Different Revit sheets may
            // export different geometry under the same block name, so isolate their definitions first.
            using (var tr = source.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (block.IsLayout || block.IsAnonymous || block.IsDependent || block.IsFromExternalReference || block.Name.StartsWith("*")) continue;
                    block.UpgradeOpen();
                    block.Name = prefix + block.Name;
                }
                tr.Commit();
            }
        }

        private static void ApplyStyles(Database db, BridgeRequest request)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var types = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                foreach (LayerAppearance style in request.LayerStyles)
                {
                    if (!layers.Has(style.Layer)) continue;
                    var layer = (LayerTableRecord)tr.GetObject(layers[style.Layer], OpenMode.ForWrite);
                    if (!string.IsNullOrWhiteSpace(style.Linetype))
                    {
                        if (!types.Has(style.Linetype)) throw new InvalidOperationException("Linetype missing in DWG: " + style.Linetype);
                        layer.LinetypeObjectId = types[style.Linetype];
                    }
                    if (style.Lineweight.HasValue)
                    {
                        if (!Enum.IsDefined(typeof(LineWeight), style.Lineweight.Value)) throw new InvalidOperationException("Invalid lineweight.");
                        layer.LineWeight = (LineWeight)style.Lineweight.Value;
                    }
                }
                tr.Commit();
            }
        }

        private static Extents3d Bounds(Database db)
        {
            Extents3d? result = null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var entity = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    if (!entity.Visible) continue;
                    Extents3d extent = entity.GeometricExtents;
                    if (result.HasValue) { var combined = result.Value; combined.AddExtents(extent); result = combined; }
                    else result = extent;
                }
            }
            return result ?? throw new InvalidOperationException("No visible model geometry.");
        }

        private static void Inspect(string path, BridgeResponse response)
        {
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                db.CloseInput(true);
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in bt)
                    {
                        var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (block.IsFromExternalReference) throw new InvalidOperationException("Output contains an external reference.");
                        if (id == bt[BlockTableRecord.ModelSpace])
                        {
                            response.ModelEntityCount = block.Cast<ObjectId>().Count();
                            foreach (ObjectId entityId in block)
                            {
                                var entity = (Entity)tr.GetObject(entityId, OpenMode.ForRead);
                                var bounds = entity.GeometricExtents;
                                response.EntityBounds.Add(new SheetPlacement { X = bounds.MinPoint.X, Y = bounds.MinPoint.Y,
                                    Width = bounds.MaxPoint.X - bounds.MinPoint.X, Height = bounds.MaxPoint.Y - bounds.MinPoint.Y });
                            }
                        }
                        else if (block.IsLayout)
                            response.PaperEntityCount += block.Cast<ObjectId>().Count(e => !(tr.GetObject(e, OpenMode.ForRead) is Viewport));
                    }
                }
            }
        }

        private static void Fixture(BridgeRequest request)
        {
            // Synthetic geometry only; never operates on a user's RVT or source DWG.
            var db = Application.DocumentManager.MdiActiveDocument.Database;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var line = new Line(new Point3d(0, 0, 0), new Point3d(100, 80, 0));
                ms.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);
                var layout = (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout), OpenMode.ForWrite);
                var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                var vp = new Viewport { CenterPoint = new Point3d(150, 100, 0), Width = 120, Height = 100,
                    ViewCenter = new Point2d(50, 40), ViewHeight = 100, ViewDirection = Vector3d.ZAxis };
                paper.AppendEntity(vp); tr.AddNewlyCreatedDBObject(vp, true); vp.On = true;
                tr.Commit();
            }
            db.UpdateExt(true);
            Application.DocumentManager.MdiActiveDocument.Editor.Regen();
            db.SaveAs(request.OutputPath, DwgVersion.AC1032);
        }
    }
}
