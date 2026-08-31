using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ChangExport.DwgProcessing;

public sealed class AutoCadProcessor
{
    public string ExecutablePath { get; }
    public AutoCadProcessor(string? executablePath = null) => ExecutablePath = executablePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "AutoCAD 2023", "acad.exe");
    public bool IsAvailable => File.Exists(ExecutablePath);

    public BridgeResponse Run(BridgeRequest request, string inputDrawing, string workingDirectory, Func<bool>? cancel = null, Action? pump = null)
    {
        if (!IsAvailable) throw new FileNotFoundException("AutoCAD 2023이 필요합니다. 설치 및 라이선스 실행 상태를 확인하세요.", ExecutablePath);
        if (!File.Exists(inputDrawing)) throw new FileNotFoundException("후처리 입력 DWG가 없습니다.", inputDrawing);
        if (File.Exists(request.OutputPath)) throw new IOException("기존 출력 파일을 덮어쓰지 않습니다: " + request.OutputPath);
        Directory.CreateDirectory(workingDirectory);
        string jobDirectory = Path.Combine(workingDirectory, "acad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        string bridge = Path.Combine(jobDirectory, "ChangExport.AutoCadBridge.dll");
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ChangExport.AutoCadBridge.dll")
            ?? throw new InvalidOperationException("AutoCAD 보조 모듈이 배포본에 없습니다."))
        using (var destination = File.Create(bridge)) resource.CopyTo(destination);
        string requestPath = Path.Combine(jobDirectory, "request.json"), responsePath = Path.Combine(jobDirectory, "response.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(false));
        string scriptPath = Path.Combine(jobDirectory, "run.scr");
        // Paths are passed through process-local environment variables, never interpolated into executable Lisp.
        // Respect AutoCAD's existing trusted-path/security policy; do not change SECURELOAD.
        File.WriteAllText(scriptPath, "(command \"_.NETLOAD\" (getenv \"CHANGEXPORT_BRIDGE\"))\nCHANGEXPORT\n_.QUIT\n_N\n", Encoding.ASCII);
        string scratchInput = Path.Combine(jobDirectory, Path.GetFileName(inputDrawing));
        foreach (string dwg in Directory.EnumerateFiles(Path.GetDirectoryName(inputDrawing)!, "*.dwg"))
            File.Copy(dwg, Path.Combine(jobDirectory, Path.GetFileName(dwg)), false);
        // Revit emits sheet/Xref files in the same native staging directory. Open only a job-local copy.
        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = jobDirectory
        };
        start.ArgumentList.Add(scratchInput); start.ArgumentList.Add("/b"); start.ArgumentList.Add(scriptPath); start.ArgumentList.Add("/nologo");
        start.Environment["CHANGEXPORT_BRIDGE"] = bridge;
        start.Environment["CHANGEXPORT_REQUEST"] = requestPath;
        start.Environment["CHANGEXPORT_RESPONSE"] = responsePath;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("AutoCAD를 시작하지 못했습니다.");
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (!process.WaitForExit(100))
            {
                pump?.Invoke();
                if (cancel?.Invoke() == true) throw new OperationCanceledException("사용자가 출력을 취소했습니다.");
                if (elapsed.Elapsed > TimeSpan.FromMinutes(5))
                    throw new TimeoutException("AutoCAD 처리 시간이 초과되었습니다. 라이선스·시작 대화상자·모듈 신뢰 설정을 확인하세요. 보안 설정은 자동 변경하지 않습니다.");
            }
            if (cancel?.Invoke() == true) throw new OperationCanceledException("사용자가 출력을 취소했습니다.");
            if (!File.Exists(responsePath)) throw new InvalidOperationException(
                $"AutoCAD가 결과를 반환하지 않았습니다 (종료 코드 {process.ExitCode}). 라이선스·모듈 로드 상태를 확인하세요. 진단 폴더: {jobDirectory}");
            var response = JsonSerializer.Deserialize<BridgeResponse>(File.ReadAllText(responsePath))
                ?? throw new InvalidDataException("AutoCAD 결과를 읽을 수 없습니다.");
            if (!response.Success) throw new InvalidOperationException(response.Message);
            if (!File.Exists(request.OutputPath) || response.ModelEntityCount <= 0 || response.PaperEntityCount != 0)
                throw new InvalidDataException("모형공간 출력 검사에 실패했습니다. 최종 파일로 게시하지 않습니다.");
            return response;
        }
        finally
        {
            if (!process.HasExited)
            {
                // Only the instance created for this job is stopped. Never terminate existing AutoCAD sessions.
                process.Kill(entireProcessTree: true); process.WaitForExit(5000);
            }
        }
    }
}
