using ChangExport.Models;

namespace ChangExport.DwgProcessing;

/// <summary>
/// RealDWG, AutoCAD .NET 또는 승인된 DWG SDK를 연결하기 위한 제품 경계입니다.
/// 향후 객체별 Remap을 위한 인터페이스입니다. 현재 시트 평면화/병합은 AutoCadProcessor가 담당합니다.
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
