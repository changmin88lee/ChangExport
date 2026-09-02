namespace ChangExport.DwgProcessing;

internal static class RevitDwgLayerNames
{
    // Revit replaces these otherwise-valid AutoCAD layer characters while
    // exporting category and subcategory names. Compare both the configured
    // name and the native DWG name through the same canonical form.
    internal static string Normalize(string value) => new(value.Select(character => character is '#' or '(' or ')' ? '_' : character).ToArray());

    internal static bool Equivalent(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
