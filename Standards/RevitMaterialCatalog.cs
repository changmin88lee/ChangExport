using Autodesk.Revit.DB;
using ChangExport.Models;

namespace ChangExport.Standards;

public static class RevitMaterialCatalog
{
    public static List<MaterialChoice> Read(Document document)
    {
        var materialIds = new FilteredElementCollector(document).WhereElementIsElementType().ToElements()
            .OfType<HostObjAttributes>()
            .Where(type => type.Category?.Id.Value is (long)BuiltInCategory.OST_Walls or (long)BuiltInCategory.OST_Floors)
            .Select(type => type.GetCompoundStructure()).Where(structure => structure is { LayerCount: >= 2 })
            .SelectMany(structure => structure!.GetLayers()).Select(layer => layer.MaterialId)
            .Where(id => id != ElementId.InvalidElementId).Distinct().ToList();
        return materialIds.Select(id => document.GetElement(id)).OfType<Material>()
        .Select(material => new MaterialChoice(material.UniqueId, material.Id.Value, material.Name))
        .OrderBy(material => material.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(material => material.ElementId)
        .ToList();
    }
}
