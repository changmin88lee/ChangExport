using ChangExport.Models;

namespace ChangExport.DwgProcessing;

/// <summary>
/// RealDWG, AutoCAD .NET 또는 승인된 DWG SDK를 연결하기 위한 제품 경계입니다.
/// Beta 0.1.0에는 Native Export까지만 포함되며 이 인터페이스 구현체는 배포하지 않습니다.
/// </summary>
public interface IDwgProcessor
{
    DwgInspectionResult Inspect(string sourcePath);
    DwgProcessResult RemapLayers(
        string sourcePath,
        string destinationPath,
        IReadOnlyDictionary<string, string> layerMap,
        CadStandardProfile profile);
    DwgProcessResult FlattenSheet(string sourcePath, string destinationPath);
    DwgProcessResult MergeAndArrange(
        IReadOnlyList<string> sheetPaths,
        string destinationPath,
        string arrangeMode,
        int columns,
        double marginMm);
}

public sealed record DwgInspectionResult(bool Success, IReadOnlyList<string> Layers, string Message);
public sealed record DwgProcessResult(bool Success, string OutputPath, string Message);
