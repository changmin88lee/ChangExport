using System.Collections.Generic;

namespace ChangExport.DwgProcessing
{
    // In-process DWG operation inputs and inspection results. No external host is used.
    public sealed class BridgeRequest
    {
        public string Operation { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string Direction { get; set; } = "Horizontal";
        public double MarginMm { get; set; }
        public List<string> Inputs { get; set; } = new List<string>();
        public List<LayerAppearance> LayerStyles { get; set; } = new List<LayerAppearance>();
        // Revit sheet DWG coordinates are millimeters. Plotter paper metadata is not a coordinate unit.
        public bool RevitSheet { get; set; }
        public bool UseLayerColors { get; set; }
        public List<ColorLayerRemap> ColorRemaps { get; set; } = new();
        public Dictionary<string, string> TextReplacements { get; set; } = new();
        public Dictionary<string, int> ExpectedRuleMatches { get; set; } = new();
    }

    public sealed class ColorLayerRemap
    {
        public int MarkerAci { get; set; }
        public string Layer { get; set; } = "";
        public int Color { get; set; }
        public string RuleId { get; set; } = "";
    }

    public sealed class LayerAppearance
    {
        public string Layer { get; set; } = "";
        public int? Color { get; set; }
        public string Linetype { get; set; } = "";
        public int? Lineweight { get; set; }
    }

    public sealed class BridgeResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public int ModelEntityCount { get; set; }
        public int PaperEntityCount { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<SheetPlacement> Placements { get; set; } = new List<SheetPlacement>();
        public List<int> PaletteRgb { get; set; } = new List<int>();
        public List<SheetPlacement> EntityBounds { get; set; } = new List<SheetPlacement>();
        public int ConvertedViewports { get; set; }
        public double ModelScale { get; set; } = 1;
        public int ExplodedInserts { get; set; }
        public int BoundaryBlocksRetained { get; set; }
        public Dictionary<string, int> CustomRuleEntityCounts { get; set; } = new();
        public Dictionary<string, double> TimingsMs { get; set; } = new();
        public int NormalizedEntityColors { get; set; }
    }

    public sealed class SheetPlacement
    {
        public string Source { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
