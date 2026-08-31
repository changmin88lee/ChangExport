using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using Color = ACadSharp.Color;

namespace ChangExport.DwgProcessing;

public sealed partial class ManagedDwgProcessor
{
    // Called only on detached output data, after filter markers and block inheritance.
    internal static void NormalizeLayerColors(CadDocument document, BridgeResponse response, Action check)
    {
        foreach (var style in document.DimensionStyles)
            style.DimensionLineColor = style.ExtensionLineColor = style.TextColor = Color.ByLayer;
        foreach (var block in document.BlockRecords)
        foreach (var entity in block.Entities)
        {
            check();
            Normalize(entity);
            if (entity is Insert insert) foreach (var attribute in insert.Attributes) Normalize(attribute);
        }

        void Normalize(Entity entity)
        {
            if (!entity.Color.IsByLayer || entity.BookColor != null) response.NormalizedEntityColors++;
            entity.Color = Color.ByLayer; entity.BookColor = null;
            if (entity is MText text) text.Value = RemoveInlineColors(text.Value);
            if (entity is Dimension dimension && dimension.GetStyleOverrideMap() is { } map)
            {
                // DIMCLRD / DIMCLRE / DIMCLRT; retain every non-color override.
                foreach (int code in new[] { 176, 177, 178 }) map.DxfProperties.Remove(code);
                dimension.SetStyleOverrideMap(map);
            }
        }
    }

    internal static string RemoveInlineColors(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                if (value[i + 1] is 'C' or 'c')
                {
                    int end = i + 2;
                    while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
                    if (end > i + 2 && end < value.Length && value[end] == ';') { i = end; continue; }
                }
                // Escaped backslashes/braces are literal text, not formatting commands.
                result.Append(value[i++]); result.Append(value[i]); continue;
            }
            result.Append(value[i]);
        }
        return result.ToString();
    }
}
