using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Standards;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class ManageSheetGroupsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            Document document = commandData.Application.ActiveUIDocument.Document;
            var store = ExportConfigurationStore.ForDocument(document); var config = store.Load();
            var sheets = SheetSetService.ReadSheets(document);
            if (sheets.Count == 0) { TaskDialog.Show("창Export", "프로젝트에 출력 가능한 시트가 없습니다."); return Result.Cancelled; }
            using var form = new SheetGroupManagerForm(sheets, SheetSetService.ReadSets(document, config, sheets));
            if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
            config.SheetSets = form.ResultSets.ToList(); store.Save(config);
            return Result.Succeeded;
        }
        catch (Exception ex) { message = ex.Message; TaskDialog.Show("세트 저장 실패", ex.Message); return Result.Failed; }
    }
}
