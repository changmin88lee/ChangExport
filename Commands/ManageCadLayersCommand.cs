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
            var document = commandData.Application.ActiveUIDocument.Document;
            var store = ExportConfigurationStore.ForDocument(document);
            var configuration = store.Load();
            var mapping = new RevitLayerMappingService(document);
            using var form = new LayerRuleManagerForm(store, configuration, RevitLayerMappingService.SetupNames(configuration), name => mapping.Read(name, configuration));
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
