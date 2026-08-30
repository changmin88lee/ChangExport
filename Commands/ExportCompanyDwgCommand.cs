using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ChangExport.Export;
using ChangExport.Models;
using ChangExport.Parameters;
using ChangExport.Rules;
using ChangExport.Standards;
using ChangExport.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ExportCompanyDwgCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document document = commandData.Application.ActiveUIDocument.Document;
        try
        {
            CommandSupport.EnsureBindings(document);
            var repository = new CadStandardRepository();
            CadStandardProfile profile = repository.LoadActive();
            IReadOnlyList<string> profileIssues = repository.Validate(profile);
            if (profileIssues.Count > 0)
            {
                TaskDialog.Show("Profile 확인 필요", string.Join("\n", profileIssues.Take(20)));
                return Result.Cancelled;
            }

            var sheetParameters = new SheetExportParameterService();
            List<ExportSheetChoice> sheetChoices = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(x => !x.IsPlaceholder)
                .Select(sheet => new ExportSheetChoice
                {
                    ElementId = sheet.Id.Value,
                    Group = sheetParameters.GetGroup(sheet),
                    Order = sheetParameters.GetOrder(sheet) ?? 0,
                    SheetNumber = sheet.SheetNumber,
                    SheetName = sheet.Name,
                    Selected = true
                }).ToList();
            if (sheetChoices.Count == 0)
            {
                TaskDialog.Show("창Export", "출력 가능한 Sheet가 없습니다.");
                return Result.Cancelled;
            }

            IList<string> setupNames = BaseExportOptions.GetPredefinedSetupNames(document);
            string defaultFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "창Export",
                DateTime.Now.ToString("yyyyMMdd"));
            string summary = BuildClassificationSummary(document, commandData.Application.ActiveUIDocument.ActiveView, profile);

            using var form = new ExportSettingsForm(sheetChoices, setupNames, profile.ProfileName, defaultFolder, summary);
            if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return Result.Cancelled;

            var service = new RevitDwgExportService();
            ExportRunResult result = service.Export(
                document,
                form.SelectedSheets,
                form.OutputFolder,
                form.SelectedSetup,
                profile.ProfileName,
                form.ArrangeMode,
                form.Columns,
                form.MarginMm);
            using var resultForm = new ExportResultForm(result);
            resultForm.ShowDialog();
            return result.FailedCount == result.Items.Count ? Result.Failed : Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("창Export 오류", ex.Message);
            return Result.Failed;
        }
    }

    private static string BuildClassificationSummary(Document document, Autodesk.Revit.DB.View activeView, CadStandardProfile profile)
    {
        var engine = new RuleEngine();
        List<ElementLayerAssignment> assignments = new FilteredElementCollector(document, activeView.Id)
            .WhereElementIsNotElementType()
            .Where(x => x.Category is not null && x is not Autodesk.Revit.DB.View)
            .Take(5000)
            .Select(x => engine.Classify(x, profile))
            .ToList();
        int manual = assignments.Count(x => x.Source == LayerDecisionSource.ManualOverride);
        int rules = assignments.Count(x => x.Source == LayerDecisionSource.RuleMatch);
        int invalid = assignments.Count(x => x.Source == LayerDecisionSource.InvalidTarget);
        return $"현재 View 분류 {assignments.Count:N0}개 (수동 {manual:N0} / Rule {rules:N0} / 확인 {invalid:N0})";
    }
}
