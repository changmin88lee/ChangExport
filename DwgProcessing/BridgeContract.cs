using System.Collections.Generic;

namespace ChangExport.DwgProcessing
{
    // Plain JSON contract shared by the .NET 8 Revit client and .NET Framework AutoCAD host.
    public sealed class BridgeRequest
    {
        public string Operation { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string Direction { get; set; } = "Horizontal";
        public double MarginMm { get; set; }
        public List<string> Inputs { get; set; } = new List<string>();
        public List<LayerAppearance> LayerStyles { get; set; } = new List<LayerAppearance>();
    }

    public sealed class LayerAppearance
    {
        public string Layer { get; set; } = "";
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
