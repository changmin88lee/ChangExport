using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Standards;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ManageCadLayersCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var repository = new CadStandardRepository();
            using var form = new LayerRuleManagerForm(repository, repository.LoadActive());
            form.ShowDialog();
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
