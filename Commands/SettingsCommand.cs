using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Standards;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var document = commandData.Application.ActiveUIDocument?.Document;
            if (document == null) { TaskDialog.Show("창Export 설정", "설정을 저장할 Revit 프로젝트를 열어 주세요."); return Result.Cancelled; }
            var store = ExportConfigurationStore.ForDocument(document); var config = store.Load();
            using var form = new ChangExportSettingsForm(document.Title, config.WideLineKeyword,
                config.SheetSpacingMm, (keyword, spacing) =>
                {
                    config.WideLineKeyword = keyword; config.SheetSpacingMm = spacing;
                    store.Save(config);
                });
            return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? Result.Succeeded : Result.Cancelled;
        }
        catch (Exception ex) { message = ex.Message; TaskDialog.Show("창Export 설정", ex.Message); return Result.Failed; }
    }
}
