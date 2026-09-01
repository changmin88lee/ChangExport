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
        public List<WideLineLayer> WideLineLayers { get; set; } = new();
        public List<FamilyBlockSource> FamilySources { get; set; } = new();
        public List<string> ExcludedLayers { get; set; } = new();
    }

    public sealed class WideLineLayer
    {
        public string NativeLayer { get; set; } = "";
        public string TargetLayer { get; set; } = "";
        public string StyleName { get; set; } = "";
        // Explicit output RGB for successfully converted ## polylines. Null keeps
        // compatibility with historical manifests that predate color preservation.
        public int? DisplayRgb { get; set; }
    }

    public sealed class FamilyBlockSource
    {
        public string Identity { get; set; } = "";
        public string Label { get; set; } = "";
        public bool IsTitleBlock { get; set; }
        public List<string> NativePrefixes { get; set; } = new();
        // Full names collected from Revit, not DWG-derived substring rules.
        public List<string> NativeLabels { get; set; } = new();
        public string ExclusionReason { get; set; } = "";
        public string Category { get; set; } = "";
        public string SourceKind { get; set; } = "LoadableFamily";
        public string PlacementType { get; set; } = "";
        public bool IsDetailGroup { get; set; }
        public List<string> NativeElementIds { get; set; } = new();
    }

    public sealed record FamilyBlockMatch(string NativeBlock, string Label, string Status);

    public sealed class FamilyBlockInfo
    {
        public string Identity { get; set; } = "";
        public string Label { get; set; } = "";
        public bool IsTitleBlock { get; set; }
        public bool IsDetailGroup { get; set; }
        public bool Processed { get; set; }
    }

    public sealed class ColorLayerRemap
    {
        public int MarkerAci { get; set; }
        public string Layer { get; set; } = "";
        public int Color { get; set; }
        public string RuleId { get; set; } = "";
        // Material filters move fill entities to the selected material layer while
        // retaining their explicit Revit display color and hatch definition.
        public bool RemapFills { get; set; }
        // Shared compound-layer boundaries keep the highest Revit material function.
        public int BoundaryPriority { get; set; }
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
        public int PreservedFillColors { get; set; }
        public int PreservedMaskingEntities { get; set; }
        public int MaterialBoundaryDuplicatesRemoved { get; set; }
        public int FilterContainerMarkersIgnored { get; set; }
        public int FilterLowerGraphicsSkipped { get; set; }
        public int ExcludedEntities { get; set; }
        public int WideLineConverted { get; set; }
        public int WideLineSkipped { get; set; }
        public int PreservedWideLineColors { get; set; }
        public Dictionary<string, int> WideLineStyleCounts { get; set; } = new();
        public Dictionary<string, FamilyBlockInfo> FamilyBlocks { get; set; } = new();
        public int FamilyBlockReferences { get; set; }
        public int FamilyBlockDefinitions { get; set; }
        public int FamilySignaturesComputed { get; set; }
        public int FamilySignaturesSkipped { get; set; }
        public int FamilySignatureCacheHits { get; set; }
        public Dictionary<string, int> FamilyBlockFallbacks { get; set; } = new();
        public List<FamilyBlockMatch> FamilyBlockMatches { get; set; } = new();
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
