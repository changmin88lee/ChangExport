using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Parameters;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ManageSheetGroupsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document document = commandData.Application.ActiveUIDocument.Document;
        try
        {
            CommandSupport.EnsureBindings(document);
            var parameterService = new SheetExportParameterService();
            List<ViewSheet> sheets = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(x => !x.IsPlaceholder).ToList();
            if (sheets.Count == 0)
            {
                TaskDialog.Show("창Export", "프로젝트에 출력 가능한 Sheet가 없습니다.");
                return Result.Cancelled;
            }

            List<SheetGroupRow> rows = sheets.Select(sheet => new SheetGroupRow
            {
                ElementId = sheet.Id.Value,
                SheetNumber = sheet.SheetNumber,
                SheetName = sheet.Name,
                ExportGroup = parameterService.GetGroup(sheet),
                ExportOrder = parameterService.GetOrder(sheet) ?? 0
            }).ToList();

            using var form = new SheetGroupManagerForm(rows);
            if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return Result.Cancelled;

            int success = 0;
            var failures = new List<string>();
            using var transaction = new Transaction(document, "Sheet DWG 그룹 저장");
            transaction.Start();
            foreach (SheetGroupRow row in form.Rows)
            {
                try
                {
                    if (document.GetElement(new ElementId(row.ElementId)) is ViewSheet sheet)
                    {
                        parameterService.Set(sheet, row.ExportGroup, row.ExportOrder <= 0 ? null : row.ExportOrder);
                        success++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{row.SheetNumber}: {ex.Message}");
                }
            }
            transaction.Commit();
            TaskDialog.Show("창Export", $"Sheet {success}개의 그룹 정보를 저장했습니다." +
                (failures.Count == 0 ? string.Empty : $"\n실패 {failures.Count}개\n" + string.Join("\n", failures.Take(8))));
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("창Export 오류", ex.Message);
            return Result.Failed;
        }
    }
}
