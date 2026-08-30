using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace ChangExport.Parameters;

public sealed class SharedParameterService
{
    private static readonly BuiltInCategory[] ModelCategories =
    {
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_Rebar
    };

    public ParameterPreparationResult EnsureBindings(Document document)
    {
        Autodesk.Revit.ApplicationServices.Application app = document.Application;
        string originalSharedParameterPath = app.SharedParametersFilename;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "ChangExport");
        string tempPath = Path.Combine(tempDirectory, "ChangExport.SharedParameters.txt");
        Directory.CreateDirectory(tempDirectory);
        if (!File.Exists(tempPath))
        {
            File.WriteAllText(tempPath, "# This is a Revit shared parameter file.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n");
        }

        try
        {
            app.SharedParametersFilename = tempPath;
            DefinitionFile file = app.OpenSharedParameterFile()
                ?? throw new InvalidOperationException("임시 공유 매개변수 파일을 열 수 없습니다.");
            DefinitionGroup group = file.Groups.get_Item("ChangExport")
                ?? file.Groups.Create("ChangExport");

            var result = new ParameterPreparationResult();
            ExternalDefinition cadLayer = GetOrCreateText(group, ParameterDefinitions.CadLayerName, ParameterDefinitions.CadLayerGuid);
            ExternalDefinition exportGroup = GetOrCreateText(group, ParameterDefinitions.ExportGroupName, ParameterDefinitions.ExportGroupGuid);
            ExternalDefinition exportOrder = GetOrCreateInteger(group, ParameterDefinitions.ExportOrderName, ParameterDefinitions.ExportOrderGuid);

            CategorySet modelSet = app.Create.NewCategorySet();
            foreach (BuiltInCategory builtInCategory in ModelCategories)
            {
                try
                {
                    Category category = document.Settings.Categories.get_Item(builtInCategory);
                    if (category.AllowsBoundParameters)
                    {
                        modelSet.Insert(category);
                        result.SupportedModelCategories.Add(category.Name);
                    }
                }
                catch
                {
                    result.UnsupportedModelCategories.Add(builtInCategory.ToString());
                }
            }

            CategorySet sheetSet = app.Create.NewCategorySet();
            sheetSet.Insert(document.Settings.Categories.get_Item(BuiltInCategory.OST_Sheets));

            Bind(document, cadLayer, modelSet);
            Bind(document, exportGroup, sheetSet);
            Bind(document, exportOrder, sheetSet);
            result.Success = true;
            return result;
        }
        finally
        {
            app.SharedParametersFilename = originalSharedParameterPath;
        }
    }

    public bool HasCadLayerBinding(Document document) => HasBinding(document, ParameterDefinitions.CadLayerGuid);
    public bool HasExportGroupBinding(Document document) => HasBinding(document, ParameterDefinitions.ExportGroupGuid);
    public bool HasExportOrderBinding(Document document) => HasBinding(document, ParameterDefinitions.ExportOrderGuid);

    private static ExternalDefinition GetOrCreateText(DefinitionGroup group, string name, Guid guid) =>
        GetOrCreate(group, name, guid, SpecTypeId.String.Text);

    private static ExternalDefinition GetOrCreateInteger(DefinitionGroup group, string name, Guid guid) =>
        GetOrCreate(group, name, guid, SpecTypeId.Int.Integer);

    private static ExternalDefinition GetOrCreate(
        DefinitionGroup group,
        string name,
        Guid guid,
        ForgeTypeId dataType)
    {
        ExternalDefinition? existing = group.Definitions
            .OfType<ExternalDefinition>()
            .FirstOrDefault(x => x.GUID == guid);
        if (existing is not null)
        {
            return existing;
        }

        var options = new ExternalDefinitionCreationOptions(name, dataType)
        {
            GUID = guid,
            Description = "창Export Beta에서 사용하는 고정 GUID 공유 매개변수",
            UserModifiable = true,
            Visible = true
        };
        return (ExternalDefinition)group.Definitions.Create(options);
    }

    private static void Bind(Document document, Definition definition, CategorySet categories)
    {
        InstanceBinding binding = document.Application.Create.NewInstanceBinding(categories);
        if (!document.ParameterBindings.Insert(definition, binding, GroupTypeId.Data))
        {
            document.ParameterBindings.ReInsert(definition, binding, GroupTypeId.Data);
        }
    }

    private static bool HasBinding(Document document, Guid guid)
    {
        DefinitionBindingMapIterator iterator = document.ParameterBindings.ForwardIterator();
        iterator.Reset();
        while (iterator.MoveNext())
        {
            if (iterator.Key is ExternalDefinition definition && definition.GUID == guid)
            {
                return true;
            }

            if (iterator.Key.Name == GetName(guid))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetName(Guid guid)
    {
        if (guid == ParameterDefinitions.CadLayerGuid) return ParameterDefinitions.CadLayerName;
        if (guid == ParameterDefinitions.ExportGroupGuid) return ParameterDefinitions.ExportGroupName;
        return ParameterDefinitions.ExportOrderName;
    }
}

public sealed class ParameterPreparationResult
{
    public bool Success { get; set; }
    public List<string> SupportedModelCategories { get; } = new();
    public List<string> UnsupportedModelCategories { get; } = new();
}
