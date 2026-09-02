using Autodesk.Revit.DB;

namespace ChangExport.Export;

/// <summary>
/// Opens a disposable local copy of a loaded linked RVT so ChangExport can use
/// the same temporary-sheet/filter transaction path as the host document.
/// The source RVT and the host link instance are never modified or unloaded.
/// </summary>
internal sealed class LinkedSheetDocumentSession : IDisposable
{
    private readonly string _stagingRoot;
    private readonly string? _sessionDirectory;
    private readonly bool _ownsDocument;
    private bool _disposed;
    public Document Document { get; }
    public string SourceName { get; }
    public string? CleanupWarning { get; private set; }

    private LinkedSheetDocumentSession(Document document, string sourceName, string stagingRoot,
        string? sessionDirectory, bool ownsDocument)
    {
        Document = document; SourceName = sourceName; _stagingRoot = stagingRoot;
        _sessionDirectory = sessionDirectory; _ownsDocument = ownsDocument;
    }

    public static LinkedSheetDocumentSession Open(Document host, Document source, string sourceName,
        string stagingRoot, List<string> warnings)
    {
        if (!source.IsLinked)
            return new LinkedSheetDocumentSession(source, sourceName, stagingRoot, null, false);
        string sourcePath = source.PathName ?? "";
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidOperationException($"링크 시트 원본 '{sourceName}'을 안전한 임시 사본으로 열 수 없습니다. " +
                "현재 버전은 로컬 또는 네트워크 경로의 로드된 RVT 링크가 필요합니다.");

        string sessionsRoot = Path.Combine(stagingRoot, "source_sessions");
        string sessionDirectory = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionDirectory);
        string temporaryPath = Path.Combine(sessionDirectory, Path.GetFileName(sourcePath));
        try
        {
            File.Copy(sourcePath, temporaryPath, false);
            RepathExternalReferences(sourcePath, temporaryPath, warnings);
            using var open = new OpenOptions { Audit = false };
            using (BasicFileInfo info = BasicFileInfo.Extract(temporaryPath))
                if (info.IsWorkshared) open.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            Document temporary = host.Application.OpenDocumentFile(
                ModelPathUtils.ConvertUserVisiblePathToModelPath(temporaryPath), open);
            if (temporary.IsReadOnly)
            {
                temporary.Close(false);
                throw new InvalidOperationException("임시 링크 문서가 읽기 전용으로 열렸습니다.");
            }
            warnings.Add($"링크 시트 원본: '{sourceName}'은 원본을 건드리지 않는 임시 RVT 사본에서 필터와 DWG 출력을 수행합니다.");
            return new LinkedSheetDocumentSession(temporary, sourceName, stagingRoot, sessionDirectory, true);
        }
        catch
        {
            DeleteSessionDirectory(stagingRoot, sessionDirectory);
            throw;
        }
    }

    private static void RepathExternalReferences(string sourcePath, string temporaryPath, List<string> warnings)
    {
        try
        {
            ModelPath sourceModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(sourcePath);
            ModelPath temporaryModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(temporaryPath);
            using TransmissionData? sourceData = TransmissionData.ReadTransmissionData(sourceModelPath);
            using TransmissionData? temporaryData = TransmissionData.ReadTransmissionData(temporaryModelPath);
            if (sourceData == null || temporaryData == null) return;
            var sourceIds = sourceData.GetAllExternalFileReferenceIds().ToHashSet();
            foreach (ElementId id in temporaryData.GetAllExternalFileReferenceIds())
            {
                if (!sourceIds.Contains(id)) continue;
                try
                {
                    using ExternalFileReference reference = sourceData.GetLastSavedReferenceData(id);
                    ModelPath absolute = reference.GetAbsolutePath();
                    bool shouldLoad = reference.GetLinkedFileStatus() == LinkedFileStatus.Loaded;
                    temporaryData.SetDesiredReferenceData(id, absolute, PathType.Absolute, shouldLoad);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ArgumentException
                    or Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    warnings.Add($"링크 시트 하위 참조 경로 유지 확인 필요: {ex.Message}");
                }
            }
            temporaryData.IsTransmitted = true;
            TransmissionData.WriteTransmissionData(temporaryModelPath, temporaryData);
        }
        catch (Exception ex)
        {
            warnings.Add($"링크 시트 하위 참조 경로 복제 확인 필요: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            if (_ownsDocument && Document.IsValidObject) Document.Close(false);
        }
        catch (Exception ex) { CleanupWarning = $"링크 임시 문서 닫기 확인 필요: '{SourceName}' · {ex.Message}"; }
        if (_sessionDirectory == null) return;
        try { DeleteSessionDirectory(_stagingRoot, _sessionDirectory); }
        catch (Exception ex)
        {
            string message = $"링크 임시 파일 정리 확인 필요: '{SourceName}' · {ex.Message}";
            CleanupWarning = CleanupWarning == null ? message : CleanupWarning + " · " + message;
        }
    }

    private static void DeleteSessionDirectory(string stagingRoot, string sessionDirectory)
    {
        string root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(sessionDirectory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("링크 임시 폴더의 안전한 범위를 확인할 수 없습니다.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
