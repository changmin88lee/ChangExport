using Autodesk.Revit.DB;
using ChangExport.Models;
using ChangExport.Parameters;

namespace ChangExport.Rules;

public sealed class RuleEngine
{
    private readonly CadLayerParameterService _cadLayerParameters = new();

    public ElementLayerAssignment Classify(Element element, CadStandardProfile profile)
    {
        string category = element.Category?.Name ?? "(카테고리 없음)";
        string builtInCategory = GetBuiltInCategoryName(element.Category);
        (string familyName, string typeName) = GetFamilyAndType(element);
        string manualLayer = _cadLayerParameters.GetValue(element);
        var validLayers = new HashSet<string>(profile.Layers.Where(x => x.Enabled).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(manualLayer))
        {
            bool valid = validLayers.Contains(manualLayer);
            return Create(element, category, familyName, typeName, manualLayer, string.Empty,
                manualLayer, valid ? LayerDecisionSource.ManualOverride : LayerDecisionSource.InvalidTarget,
                valid ? string.Empty : "수동 Layer가 Profile에 없습니다.");
        }

        List<DwgRule> matches = profile.Rules
            .Select((rule, index) => new { rule, index })
            .Where(x => x.rule.Enabled && CategoryMatches(x.rule.Category, category, builtInCategory))
            .Where(x => IsMatch(ReadRuleValue(element, x.rule.ParameterName, familyName, typeName), x.rule))
            .OrderByDescending(x => x.rule.Priority)
            .ThenBy(x => x.index)
            .Select(x => x.rule)
            .ToList();

        if (matches.Count > 0)
        {
            DwgRule selected = matches[0];
            bool valid = validLayers.Contains(selected.TargetLayer);
            bool conflict = matches.Skip(1).Any(x => x.Priority == selected.Priority &&
                !string.Equals(x.TargetLayer, selected.TargetLayer, StringComparison.OrdinalIgnoreCase));
            string warning = !valid
                ? "Rule Target Layer가 Profile에 없습니다."
                : conflict ? "같은 우선순위의 Rule 충돌이 있습니다." : string.Empty;
            return Create(element, category, familyName, typeName, string.Empty, selected.Name,
                selected.TargetLayer, valid ? LayerDecisionSource.RuleMatch : LayerDecisionSource.InvalidTarget, warning);
        }

        return Create(element, category, familyName, typeName, string.Empty, string.Empty,
            builtInCategory.Length > 0 ? builtInCategory : category,
            LayerDecisionSource.DefaultMapping, string.Empty);
    }

    private static ElementLayerAssignment Create(
        Element element, string category, string family, string type, string manual,
        string rule, string target, LayerDecisionSource source, string warning) => new()
    {
        ElementId = element.Id.Value,
        Category = category,
        FamilyName = family,
        TypeName = type,
        ManualLayer = manual,
        MatchedRule = rule,
        TargetLayer = target,
        Source = source,
        Warning = warning
    };

    private static bool CategoryMatches(string ruleCategory, string localizedName, string builtInName) =>
        string.IsNullOrWhiteSpace(ruleCategory)
        || string.Equals(ruleCategory, localizedName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ruleCategory, builtInName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ruleCategory, "OST_" + builtInName, StringComparison.OrdinalIgnoreCase);

    private static string GetBuiltInCategoryName(Category? category)
    {
        if (category is null) return string.Empty;
        string value = ((BuiltInCategory)category.Id.Value).ToString();
        return value.StartsWith("OST_", StringComparison.Ordinal) ? value[4..] : value;
    }

    private static (string Family, string Type) GetFamilyAndType(Element element)
    {
        ElementType? type = element.Document.GetElement(element.GetTypeId()) as ElementType;
        string typeName = type?.Name ?? string.Empty;
        string familyName = type switch
        {
            FamilySymbol symbol => symbol.FamilyName,
            null => string.Empty,
            _ => type.FamilyName
        };
        return (familyName, typeName);
    }

    private static string ReadRuleValue(Element element, string parameterName, string family, string type)
    {
        if (parameterName.Equals("Type Name", StringComparison.OrdinalIgnoreCase)) return type;
        if (parameterName.Equals("Family Name", StringComparison.OrdinalIgnoreCase)) return family;
        if (parameterName.Equals("Category", StringComparison.OrdinalIgnoreCase)) return element.Category?.Name ?? string.Empty;

        Parameter? parameter = element.LookupParameter(parameterName);
        if (parameter is null)
        {
            parameter = (element.Document.GetElement(element.GetTypeId()) as ElementType)?.LookupParameter(parameterName);
        }
        if (parameter is null) return string.Empty;
        return parameter.StorageType switch
        {
            StorageType.String => parameter.AsString() ?? string.Empty,
            StorageType.Integer => parameter.AsValueString() ?? parameter.AsInteger().ToString(),
            StorageType.Double => parameter.AsValueString() ?? parameter.AsDouble().ToString("G"),
            StorageType.ElementId => parameter.AsValueString() ?? parameter.AsElementId().Value.ToString(),
            _ => string.Empty
        };
    }

    private static bool IsMatch(string actual, DwgRule rule) => rule.Operator switch
    {
        RuleOperator.Equals => string.Equals(actual, rule.CompareValue, StringComparison.OrdinalIgnoreCase),
        RuleOperator.Contains => actual.Contains(rule.CompareValue ?? string.Empty, StringComparison.OrdinalIgnoreCase),
        RuleOperator.StartsWith => actual.StartsWith(rule.CompareValue ?? string.Empty, StringComparison.OrdinalIgnoreCase),
        RuleOperator.IsEmpty => string.IsNullOrWhiteSpace(actual),
        _ => false
    };
}
