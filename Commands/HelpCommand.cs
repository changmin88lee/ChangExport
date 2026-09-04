using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class HelpCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        using var form = new ChangExportHelpForm();
        form.ShowDialog();
        return Result.Succeeded;
    }
}
