using Autodesk.Revit.DB;
using ChangExport.DwgProcessing;
using ChangExport.Models;
using Color = Autodesk.Revit.DB.Color;
using View = Autodesk.Revit.DB.View;

namespace ChangExport.Export;

/// <summary>Applies export-only overrides to independent copies. No source view is edited.</summary>
internal static class TemporaryFilterExport
{
    internal sealed class Result
    {
        public string Drawing { get; set; } = "";
        public List<ColorLayerRemap> Remaps { get; } = new();
        public Dictionary<string, string> TextReplacements { get; } = new();
        public Dictionary<string, int> MatchedElements { get; } = new();
    }

    public static Result Export(Document document, ViewSheet source, DWGExportOptions options,
        IReadOnlyList<RevitLayerRow> rows, string baselineDirectory, string directory, List<string> warnings, Func<bool> cancel)
    {
        var result = new Result();
        if (options.PropOverrides == PropOverrideMode.ByLayer)
            throw new InvalidOperationException("선택한 Revit 출력 설정이 객체 재지정을 제외합니다. 필터에는 재지정 BYENTITY 또는 재지정별 새 레이어 설정이 필요합니다. 임의로 기본 도면 표현을 바꾸지 않았습니다.");
        var rules = rows.Where(r => r.IsCustom).ToList();
        var used = ManagedDwgProcessor.UsedColorIndices(Directory.GetFiles(baselineDirectory, "*.dwg"));
        used.UnionWith(rows.SelectMany(r => new[] { r.Color, r.CutColor }).Where(i => i is >= 1 and <= 255));
        int Rgb(int index) { var c = new ACadSharp.Color((short)index); return (c.R << 16) | (c.G << 8) | c.B; }
        var usedRgb = used.Select(Rgb).ToHashSet();
        // ACI has duplicate RGB entries; use one representative and exact RGB matching.
        var available = Enumerable.Range(1, 255).GroupBy(Rgb).Where(g => !usedRgb.Contains(g.Key)).Select(g => g.First()).ToQueue();
        if (available.Count < rules.Count * 2)
            throw new InvalidOperationException("기존 도면 색상과 충돌하지 않는 필터 식별색이 부족합니다. 원본 출력은 보존했습니다.");
        var markers = new Dictionary<string, (Color Projection, Color Cut)>();
        foreach (var rule in rules)
        {
            int projection = available.Dequeue(), cut = available.Dequeue();
            Color ToRevit(int index) { var c = new ACadSharp.Color((short)index); return new Color(c.R, c.G, c.B); }
            markers[rule.RuleId] = (ToRevit(projection), ToRevit(cut));
            result.Remaps.Add(new ColorLayerRemap { MarkerAci = projection, Layer = rule.Layer, Color = rule.Color, RuleId = rule.RuleId });
            result.Remaps.Add(new ColorLayerRemap { MarkerAci = cut, Layer = rule.CutLayer, Color = rule.CutColor, RuleId = rule.RuleId });
            result.MatchedElements[rule.RuleId] = 0;
        }
        using var group = new TransactionGroup(document, "창Export 임시 필터 출력 (복구)");
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("임시 출력 트랜잭션을 시작할 수 없습니다.");
        var temporaryIds = new List<ElementId>();
        try
        {
            ElementId sheetId;
            using (var transaction = new Transaction(document, "창Export 복제 시트 필터"))
            {
                transaction.Start();
                if (!source.CanBeDuplicated(SheetDuplicateOption.DuplicateSheetWithViewsAndDetailing))
                    throw new InvalidOperationException("상세를 포함한 독립 시트 복제가 불가능합니다.");
                sheetId = source.Duplicate(SheetDuplicateOption.DuplicateSheetWithViewsAndDetailing);
                var sheet = (ViewSheet)document.GetElement(sheetId); temporaryIds.Add(sheet.Id);
                string token = "CE_TMP_" + Guid.NewGuid().ToString("N");
                sheet.SheetNumber = token; sheet.Name = source.Name;
                var scheduled = sheet.get_Parameter(BuiltInParameter.SHEET_SCHEDULED);
                if (scheduled is { IsReadOnly: false }) scheduled.Set(0);
                result.TextReplacements[token] = source.SheetNumber;
                document.Regenerate();
                var originals = source.GetAllPlacedViews().ToHashSet();
                var views = sheet.GetAllPlacedViews().Select(id => document.GetElement(id)).OfType<View>().ToList();
                var sourcePorts = source.GetAllViewports().Select(id => (Viewport)document.GetElement(id)).ToList();
                foreach (Viewport port in sheet.GetAllViewports().Select(id => (Viewport)document.GetElement(id)))
                {
                    var original = sourcePorts.FirstOrDefault(p => p.GetBoxCenter().DistanceTo(port.GetBoxCenter()) < 1e-6);
                    if (original == null || originals.Contains(port.ViewId)) continue;
                    var originalView = (View)document.GetElement(original.ViewId); var copy = (View)document.GetElement(port.ViewId);
                    var title = copy.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                    string name = originalView.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString() ?? "";
                    if (title is { IsReadOnly: false }) title.Set(string.IsNullOrEmpty(name) ? originalView.Name : name);
                }
                views.Add(sheet);
                foreach (View view in views)
                {
                    if (cancel()) throw new OperationCanceledException();
                    if (originals.Contains(view.Id))
                    {
                        warnings.Add($"필터 제외: 공유 뷰 '{view.Name}'는 독립 복제되지 않아 원본 표시를 유지했습니다."); continue;
                    }
                    temporaryIds.Add(view.Id);
                    if (!view.AreGraphicsOverridesAllowed()) { warnings.Add($"필터 제외: '{view.Name}'는 그래픽 재지정을 지원하지 않습니다."); continue; }
                    // Detach only a temporary copy; the source template and view remain untouched.
                    if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
                    document.Regenerate();
                    foreach (Element element in new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType().ToElements())
                    {
                        if (element is ImportInstance || element.Category == null) continue;
                        string typeName = document.GetElement(element.GetTypeId())?.Name ?? "";
                        var rule = rules.FirstOrDefault(r => r.Matches(element.Category.Name, typeName));
                        if (rule == null) continue;
                        var marker = markers[rule.RuleId];
                        using var settings = view.GetElementOverrides(element.Id);
                        settings.SetProjectionLineColor(marker.Projection).SetCutLineColor(marker.Cut)
                            .SetSurfaceForegroundPatternColor(marker.Projection).SetSurfaceBackgroundPatternColor(marker.Projection)
                            .SetCutForegroundPatternColor(marker.Cut).SetCutBackgroundPatternColor(marker.Cut);
                        view.SetElementOverrides(element.Id, settings); result.MatchedElements[rule.RuleId]++;
                    }
                }
                if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("임시 필터 적용에 실패했습니다.");
            }
            if (result.MatchedElements.Values.Sum() == 0)
            {
                result.Drawing = Path.Combine(baselineDirectory, "sheet.dwg"); result.Remaps.Clear(); result.TextReplacements.Clear();
                warnings.Add("필터: 일치하는 현재 프로젝트 유형이 없어 기본 카테고리 DWG를 유지했습니다. 링크 내부 객체는 호스트 유형 필터 대상이 아닙니다.");
                return result;
            }
            if (cancel()) throw new OperationCanceledException();
            Directory.CreateDirectory(directory);
            using var filteredOptions = new DWGExportOptions(options);
            // Keep the selected override/color behavior for unmatched objects.
            if (!document.Export(directory, "sheet", new List<ElementId> { sheetId }, filteredOptions))
                throw new IOException("필터 복제 시트의 DWG 생성에 실패했습니다.");
            result.Drawing = Path.Combine(directory, "sheet.dwg");
            if (!File.Exists(result.Drawing)) throw new IOException("필터 시트 DWG가 없습니다.");
            foreach (var rule in rules)
                warnings.Add($"필터 판정: {rule.Category} / 유형 이름 포함 '{rule.TypeNameContains}' · Revit 객체 {result.MatchedElements[rule.RuleId]:N0}개 (최종 DWG 반영 개수는 별도 기록)");
        }
        finally
        {
            try
            {
                if (group.GetStatus() == TransactionStatus.Started && group.RollBack() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("트랜잭션 그룹 복구 상태를 확인할 수 없습니다.");
                if (temporaryIds.Any(id => document.GetElement(id) != null))
                    throw new InvalidOperationException("임시 시트/뷰 잔존이 확인되었습니다.");
            }
            catch (Exception ex) { throw new TemporaryExportRestoreException("임시 출력 요소 복구 확인에 실패했습니다. 출력을 중단합니다.", ex); }
        }
        return result;
    }

    private static Queue<T> ToQueue<T>(this IEnumerable<T> source) => new(source);
}

internal sealed class TemporaryExportRestoreException(string message, Exception inner) : InvalidOperationException(message, inner);
