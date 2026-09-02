using System.Text;
using System.Text.Json;
using ChangExport.App;

namespace ChangExport.Export;

public static class ExportDiagnosticText
{
    public static string Build(ExportRunResult result)
    {
        var text = new StringBuilder();
        text.AppendLine("창Export DWG 출력 진단");
        text.AppendLine("버전: " + ProductInfo.Version);
        text.AppendLine("진단 저장 시각: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        text.AppendLine($"저장 성공: {result.SuccessCount} · 실패: {result.FailedCount} · 취소: {result.Cancelled}");
        text.AppendLine("출력 폴더: " + result.OutputFolder);
        text.AppendLine("Manifest: " + result.ManifestPath);
        text.AppendLine("임시 DWG/진단 폴더: " + result.WorkFolder);
        text.AppendLine();

        for (int index = 0; index < result.Items.Count; index++)
        {
            ExportItemResult item = result.Items[index];
            text.AppendLine($"[세트 {index + 1}] {item.SheetNumber} {item.SheetName}".TrimEnd());
            text.AppendLine("상태: " + (item.Success ? "성공" : "실패"));
            text.AppendLine("메시지: " + item.Message);
            text.AppendLine($"모형공간 객체: {item.ModelEntityCount:N0} · 배치공간 객체: {item.PaperEntityCount:N0}");
            text.AppendLine("경고:");
            if (item.Warnings.Count == 0) text.AppendLine("  없음");
            else foreach (string warning in item.Warnings.Distinct()) text.AppendLine("  - " + warning);
            text.AppendLine("오류 상세:");
            text.AppendLine(string.IsNullOrWhiteSpace(item.ErrorDetails) ? "  없음" : item.ErrorDetails.TrimEnd());
            text.AppendLine("시트 진단:");
            text.AppendLine(item.SheetDiagnostics.Count == 0 ? "  없음" : JsonSerializer.Serialize(item.SheetDiagnostics,
                new JsonSerializerOptions { WriteIndented = true }));
            text.AppendLine("처리 시간(ms):");
            if (item.TimingsMs.Count == 0) text.AppendLine("  없음");
            else foreach (var timing in item.TimingsMs.OrderBy(pair => pair.Key))
                text.AppendLine($"  {timing.Key}: {timing.Value:F3}");
            text.AppendLine();
        }
        return text.ToString();
    }
}
