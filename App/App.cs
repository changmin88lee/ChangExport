using System.Reflection;
using Autodesk.Revit.UI;
using ChangExport.Commands;

namespace ChangExport.App;

public sealed class App : IExternalApplication
{
    public const string TabName = "창Export";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch
        {
            // 이미 만들어진 탭은 그대로 사용한다.
        }

        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        RibbonPanel layerPanel = GetOrCreatePanel(application, "레이어");
        RibbonPanel exportPanel = GetOrCreatePanel(application, "DWG 출력");
        RibbonPanel supportPanel = GetOrCreatePanel(application, "지원");

        AddButton(layerPanel, "AssignCadLayer", "CAD Layer\n지정", assemblyPath,
            typeof(AssignCadLayerCommand), "선택한 객체에 회사 CAD Layer를 지정합니다.");
        AddButton(layerPanel, "ClearCadLayer", "CAD Layer\n제거", assemblyPath,
            typeof(ClearCadLayerCommand), "선택한 객체의 CAD_LAYER 값을 비웁니다.");
        AddButton(layerPanel, "ManageLayers", "Layer/Rule\n관리", assemblyPath,
            typeof(ManageCadLayersCommand), "회사 Layer Profile과 자동 분류 Rule을 관리합니다.");

        AddButton(exportPanel, "ManageSheets", "Sheet 그룹\n관리", assemblyPath,
            typeof(ManageSheetGroupsCommand), "시트의 CAD_EXPORT_GROUP과 CAD_EXPORT_ORDER를 편집합니다.");
        AddButton(exportPanel, "ExportCompanyDwg", "회사 DWG\n출력", assemblyPath,
            typeof(ExportCompanyDwgCommand), "그룹과 순서를 확인한 뒤 Revit Native DWG를 출력합니다.");

        AddButton(supportPanel, "DwgDiagnostics", "기술\n진단", assemblyPath,
            typeof(DwgPrototypeDiagnosticsCommand), "프로젝트 매개변수와 DWG Export 준비 상태를 점검합니다.");
        AddButton(supportPanel, "ChangExportHelp", "도움말", assemblyPath,
            typeof(HelpCommand), "창Export 베타 사용 방법과 현재 지원 범위를 표시합니다.");

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static RibbonPanel GetOrCreatePanel(UIControlledApplication application, string name) =>
        application.GetRibbonPanels(TabName).FirstOrDefault(x => x.Name == name)
        ?? application.CreateRibbonPanel(TabName, name);

    private static void AddButton(
        RibbonPanel panel,
        string internalName,
        string text,
        string assemblyPath,
        Type commandType,
        string toolTip)
    {
        if (panel.GetItems().Any(x => x.Name == internalName))
        {
            return;
        }

        var data = new PushButtonData(internalName, text, assemblyPath, commandType.FullName);
        var button = (PushButton)panel.AddItem(data);
        button.ToolTip = toolTip;
        button.LongDescription = "Revit 2026용 창Export Beta 기능입니다.";
    }
}
