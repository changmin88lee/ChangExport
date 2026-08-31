using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Export;
using ChangExport.Standards;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ExportCompanyDwgCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            Document document = commandData.Application.ActiveUIDocument.Document;
            var store = ExportConfigurationStore.ForDocument(document); var configuration = store.Load();
            var sheets = SheetSetService.ReadSheets(document);
            if (sheets.Count == 0) { TaskDialog.Show("창Export", "출력 가능한 시트가 없습니다."); return Result.Cancelled; }
            var mapping = new RevitLayerMappingService(document);
            using var settings = new ExportSettingsForm(sheets, SheetSetService.ReadSets(document, configuration, sheets), mapping.SetupNames,
                configuration.SelectedSetup, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "창Export", DateTime.Now.ToString("yyyyMMdd")),
                sets => { configuration.SheetSets = sets.Select(s => s.Copy()).ToList(); store.Save(configuration); });
            if (settings.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
            var layers = mapping.Read(settings.SelectedSetup, configuration);
            configuration.SelectedSetup = settings.SelectedSetup; store.Save(configuration);
            using var progress = new ExportProgressForm((report, cancel, pump) => new RevitDwgExportService().Export(document,
                settings.SelectedSets, settings.OutputFolder, settings.SelectedSetup, layers, report, cancel, pump));
            progress.ShowDialog();
            if (progress.Failure is not null) throw progress.Failure;
            var result = progress.Result ?? throw new InvalidOperationException("출력 결과가 없습니다.");
            using var resultForm = new ExportResultForm(result); resultForm.ShowDialog();
            return result.Cancelled ? Result.Cancelled : result.SuccessCount == 0 ? Result.Failed : Result.Succeeded;
        }
        catch (Exception ex) { message = ex.Message; TaskDialog.Show("창Export 출력 실패", ex.Message); return Result.Failed; }
    }
}
