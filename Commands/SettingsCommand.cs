using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Standards;
using ChangExport.UI;
using ChangExport.Export;
using ChangExport.Models;

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
            var sheets = SheetSetService.ReadSheets(document);
            var selectedFamilies = config.AdditionalBlockFamilyIds.ToList();
            using var form = new ChangExportSettingsForm(document.Title, config.WideLineKeyword,
                SheetSetService.ReadSets(document, config, sheets), (keyword, sets) =>
                {
                    config.WideLineKeyword = keyword; config.SheetSets = sets.Select(s => s.Copy()).ToList();
                    config.AdditionalBlockFamilyIds = selectedFamilies.ToList();
                    store.Save(config);
                }, owner =>
                {
                    var choices = ExportGeometryOptions.ReadFamilies(document).Where(f => f.CanSelectAdditional)
                        .DistinctBy(f => f.FamilyIdentity).Select(f => new BlockFamilyChoice(f.FamilyIdentity, f.FamilyName, f.Category)).ToList();
                    using var families = new FamilyBlockSelectionForm(choices, selectedFamilies);
                    if (families.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK) selectedFamilies = families.SelectedIds.ToList();
                });
            return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? Result.Succeeded : Result.Cancelled;
        }
        catch (Exception ex) { message = ex.Message; TaskDialog.Show("창Export 설정", ex.Message); return Result.Failed; }
    }
}
