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
        // Native Geometry Engine (NGE): inputDrawing remains the only geometry
        // source. This detached filtered DWG supplies classification markers only.
        public string FilterReferencePath { get; set; } = "";
        public List<ColorLayerRemap> ColorRemaps { get; set; } = new();
        public List<MaterialAppearanceRemap> MaterialAppearanceRemaps { get; set; } = new();
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
        // Shared compound-layer boundaries keep the interior/bottom layer in
        // Revit's exterior-to-interior or top-to-bottom compound order.
        public int BoundaryPriority { get; set; }
        // Optional native category-layer gate. Material Part markers may only
        // classify native entities that came from the same Revit source category.
        public List<string> SourceLayers { get; set; } = new();
        // Ownership-only compound Part marker. It competes by the real compound
        // layer index but never changes the selected Native entity's layer.
        public bool PreserveNative { get; set; }
        // Diagnostic-only provenance. These fields never participate in layer
        // selection; they explain which Revit compound layers shared a marker.
        public List<string> DiagnosticSourceCategories { get; set; } = new();
        public List<int> DiagnosticCompoundLayerIndices { get; set; } = new();
        public List<string> DiagnosticCompoundLayerFunctions { get; set; } = new();
        public List<string> DiagnosticMaterialNames { get; set; } = new();
        public int DiagnosticSourceElementCount { get; set; }
        public List<string> DiagnosticSourceElementIds { get; set; } = new();
    }

    /// <summary>
    /// Matches the native DWG appearance of a material that belongs to a linked
    /// Revit document. Linked elements cannot receive host-side element overrides
    /// or Parts, so an unambiguous hatch signature is consumed after xrefs bind.
    /// </summary>
    public sealed class MaterialAppearanceRemap
    {
        public string Pattern { get; set; } = "";
        public bool IsSolid { get; set; }
        public int DisplayRgb { get; set; }
        public string Layer { get; set; } = "";
        public int Color { get; set; }
        public string RuleId { get; set; } = "";
        public string MaterialName { get; set; } = "";
        public int BoundaryPriority { get; set; }
        public bool AllowColorOnly { get; set; }
        public List<MaterialPatternLine> PatternLines { get; set; } = new();
    }

    public sealed class MaterialPatternLine
    {
        public double SpacingMm { get; set; }
        public double ShiftMm { get; set; }
        public List<double> SegmentsMm { get; set; } = new();
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
        public int LinkedMaterialFillsRemapped { get; set; }
        public int LinkedMaterialBoundariesRemapped { get; set; }
        public int FilterContainerMarkersIgnored { get; set; }
        public int FilterLowerGraphicsSkipped { get; set; }
        public string GeometrySource { get; set; } = "FilteredGeometry";
        public int NativeOverlayMatchedEntities { get; set; }
        public int NativeOverlayUnmatchedMarkers { get; set; }
        public int NativeOverlayAmbiguousMarkers { get; set; }
        public int NativeOverlayPartialLinesSplit { get; set; }
        public List<NativeOverlayRuleDiagnostic> NativeOverlayRuleDiagnostics { get; set; } = new();
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

    /// <summary>
    /// Read-only trace of one temporary marker through the Native Geometry
    /// overlay. It is serialized into the manifest and diagnostic text only.
    /// </summary>
    public sealed class NativeOverlayRuleDiagnostic
    {
        public int MarkerAci { get; set; }
        public string RuleId { get; set; } = "";
        public string TargetLayer { get; set; } = "";
        public int BoundaryPriority { get; set; }
        public bool RemapFills { get; set; }
        public bool Wrapping { get; set; }
        public bool PreserveNative { get; set; }
        public int ExpectedRevitMatches { get; set; }
        public List<string> SourceLayers { get; set; } = new();
        public List<string> SourceCategories { get; set; } = new();
        public List<int> CompoundLayerIndices { get; set; } = new();
        public List<string> CompoundLayerFunctions { get; set; } = new();
        public List<string> MaterialNames { get; set; } = new();
        public int SourceElementCount { get; set; }
        public List<string> SourceElementIds { get; set; } = new();
        public int ClassifiedEntities { get; set; }
        public int ClassifiedLines { get; set; }
        public int UnsupportedMarkerEntities { get; set; }
        public int UniqueMarkerSignatures { get; set; }
        public int ExactNativeSignatures { get; set; }
        public int MissingNativeSignatures { get; set; }
        public int UniqueMarkerLineSignatures { get; set; }
        public int ExactNativeLineSignatures { get; set; }
        public int CollinearNativeCandidates { get; set; }
        public int AcceptedSourceCandidates { get; set; }
        public int RejectedSourceCandidates { get; set; }
        public int FullLineAssignments { get; set; }
        public int PartialLineAssignments { get; set; }
        public int AppliedEntities { get; set; }
        public int PreservedEntities { get; set; }
        public Dictionary<string, int> MarkerEntityTypes { get; set; } = new();
        public Dictionary<string, int> CandidateNativeLayers { get; set; } = new();
        public Dictionary<string, int> RejectedNativeLayers { get; set; } = new();
        public Dictionary<string, int> AppliedNativeLayers { get; set; } = new();
        public Dictionary<string, int> RejectionReasons { get; set; } = new();
        public List<NativeOverlayDiagnosticSample> Samples { get; set; } = new();
        public int OmittedSamples { get; set; }
    }

    public sealed class NativeOverlayDiagnosticSample
    {
        public string Result { get; set; } = "";
        public string MarkerGeometry { get; set; } = "";
        public string NativeLayer { get; set; } = "";
        public string NativeEntityType { get; set; } = "";
        public string Detail { get; set; } = "";
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
