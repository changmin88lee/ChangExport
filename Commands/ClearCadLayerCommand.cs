using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Parameters;

namespace ChangExport.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ClearCadLayerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uiDocument = commandData.Application.ActiveUIDocument;
        Document document = uiDocument.Document;
        List<Element> selected = uiDocument.Selection.GetElementIds()
            .Select(document.GetElement).Where(x => x is not null).Cast<Element>().ToList();
        if (selected.Count == 0)
        {
            TaskDialog.Show("창Export", "먼저 CAD_LAYER 값을 제거할 객체를 선택하세요.");
            return Result.Cancelled;
        }

        try
        {
            CommandSupport.EnsureBindings(document);
            using var transaction = new Transaction(document, "CAD Layer 제거");
            transaction.Start();
            ParameterWriteResult result = new CadLayerParameterService().SetValue(selected, string.Empty);
            transaction.Commit();
            TaskDialog.Show("창Export", CommandSupport.BuildWriteSummary(result));
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
