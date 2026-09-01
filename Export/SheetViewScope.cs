using Autodesk.Revit.DB;
using ChangExport.Models;

namespace ChangExport.Export;

internal static class SheetViewScope
{
    internal static string Determine(Document document, ViewSheet sheet, List<string> warnings)
    {
        var kinds = new HashSet<string>();
        foreach (var view in sheet.GetAllPlacedViews().Select(id => document.GetElement(id)).OfType<ViewPlan>())
        {
            if (document.GetElement(view.GetTypeId()) is not ViewFamilyType type) continue;
            string? kind = type.ViewFamily switch
            {
                ViewFamily.StructuralPlan => ViewLayerScope.StructuralPlan,
                ViewFamily.CeilingPlan => ViewLayerScope.CeilingPlan,
                ViewFamily.FloorPlan => ViewLayerScope.ArchitecturePlan,
                _ => null
            };
            if (kind != null) kinds.Add(kind);
        }
        if (kinds.Count > 1)
            throw new InvalidOperationException($"시트 {sheet.SheetNumber}에 건축·구조·천장 평면도가 함께 배치되어 레이어 설정을 하나로 결정할 수 없습니다.");
        if (kinds.Count == 1) return kinds.Single();
        warnings.Add($"평면도 구분: 시트 {sheet.SheetNumber}에 건축·구조·천장 평면도가 없어 건축평면도 설정을 사용했습니다.");
        return ViewLayerScope.ArchitecturePlan;
    }
}
