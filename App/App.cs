using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

        AddButton(layerPanel, "ManageLayers", "DWG 레이어\n설정", assemblyPath,
            typeof(ManageCadLayersCommand), "Revit의 전체 DWG 카테고리 매핑과 색상을 설정합니다.", "layers");

        AddButton(exportPanel, "ManageSheets", "시트 세트\n구성", assemblyPath,
            typeof(ManageSheetGroupsCommand), "시트를 선택해 세트를 구성하고 순서와 가로·세로 배치를 설정합니다.", "sheet-sets");
        AddButton(exportPanel, "ExportCompanyDwg", "DWG\n출력", assemblyPath,
            typeof(ExportCompanyDwgCommand), "외부 CAD 프로그램 없이 내장 엔진으로 세트별 모형공간 DWG를 출력합니다.", "dwg-export");

        AddButton(supportPanel, "ChangExportHelp", "도움말", assemblyPath,
            typeof(HelpCommand), "창Export 기능별 사용 방법과 현재 지원 범위를 표시합니다.", "help");
        AddButton(supportPanel, "ChangExportSettings", "설정", assemblyPath,
            typeof(SettingsCommand), "전역폭 판별 문자열과 시트 세트별 배치 간격을 설정합니다.", "settings");
        AddButton(supportPanel, "DwgDiagnostics", "기술\n진단", assemblyPath,
            typeof(DwgPrototypeDiagnosticsCommand), "프로젝트 매개변수와 DWG Export 준비 상태를 점검합니다.", "diagnostics");

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
        string toolTip,
        string iconName)
    {
        if (panel.GetItems().Any(x => x.Name == internalName))
        {
            return;
        }

        var data = new PushButtonData(internalName, text, assemblyPath, commandType.FullName);
        var button = (PushButton)panel.AddItem(data);
        button.ToolTip = toolTip;
        button.LongDescription = $"Revit 2026용 창Export {ProductInfo.Version} · Revit 독립 애드인 · 외부 CAD 설치 불필요";
        button.Image = LoadIcon(iconName, 16);
        button.LargeImage = LoadIcon(iconName, 32);
    }

    private static ImageSource LoadIcon(string iconName, int size)
    {
        string assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "ChangExport";
        var uri = new Uri($"pack://application:,,,/{assemblyName};component/Assets/{iconName}-{size}.png", UriKind.Absolute);
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
