using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Parameters;
using ChangExport.Standards;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class AssignCadLayerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDocument = commandData.Application.ActiveUIDocument;
        Document document = uiDocument.Document;
        List<Element> selected = uiDocument.Selection.GetElementIds()
            .Select(document.GetElement)
            .Where(x => x is not null)
            .Cast<Element>()
            .ToList();
        if (selected.Count == 0)
        {
            TaskDialog.Show("창Export", "먼저 CAD Layer를 지정할 Revit 객체를 선택하세요.");
            return Result.Cancelled;
        }

        try
        {
            CommandSupport.EnsureBindings(document);
            var parameterService = new CadLayerParameterService();
            List<string> currentValues = selected.Select(parameterService.GetValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string summary = currentValues.Count == 1
                ? (currentValues[0].Length == 0 ? "미지정" : currentValues[0])
                : "혼합값";
            var repository = new CadStandardRepository();
            using var form = new LayerAssignForm(selected.Count, summary, repository.LoadActive().Layers);
            if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return Result.Cancelled;

            using var transaction = new Transaction(document, form.SelectedLayer.Length == 0 ? "CAD Layer 제거" : "CAD Layer 지정");
            transaction.Start();
            ParameterWriteResult writeResult = parameterService.SetValue(selected, form.SelectedLayer);
            transaction.Commit();
            TaskDialog.Show("창Export", CommandSupport.BuildWriteSummary(writeResult));
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
