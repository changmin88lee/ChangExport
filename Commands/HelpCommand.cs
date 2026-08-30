using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ChangExport.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class HelpCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var dialog = new TaskDialog("창Export Beta 0.1.0")
        {
            MainInstruction = "한국형 DWG Export 프로토타입",
            MainContent =
                "1. 객체를 선택하고 CAD Layer 지정\n" +
                "2. Layer/Rule 관리에서 회사 Profile 확인\n" +
                "3. Sheet 그룹 관리에서 Group과 Order 저장\n" +
                "4. 회사 DWG 출력에서 Sheet와 폴더 선택\n" +
                "5. 결과 폴더의 Native DWG와 Manifest 확인",
            ExpandedContent =
                "현재 Beta는 CAD_LAYER 공유 매개변수, 기본 Rule Engine, Sheet 그룹, Revit Native DWG Export를 제공합니다. " +
                "DWG Layer Rename/Merge, 여러 Sheet의 단일 DWG Model Space 병합은 RealDWG 또는 승인된 DWG SDK가 연결된 뒤 활성화됩니다.",
            CommonButtons = TaskDialogCommonButtons.Close
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
