using ChangExport.DwgProcessing;

namespace ChangExport.Export;

// At most two detached DWG jobs. All Revit calls and UI callbacks stay on the caller.
internal sealed class DwgPreparationQueue : IDisposable
{
    private readonly Func<bool> _cancel;
    private readonly Action _pump;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<(string Sheet, Task<PreparedDrawing> Work)> _pending = new();
    private readonly List<(string Sheet, PreparedDrawing Drawing)> _ready = new();
    public DwgPreparationQueue(Func<bool> cancel, Action pump) { _cancel = cancel; _pump = pump; }

    public void Enqueue(string sheet, BridgeRequest request, string input, string fallback)
    {
        if (_pending.Count >= Math.Min(2, Math.Max(1, Environment.ProcessorCount))) CompleteNext();
        if (_cancel()) throw new OperationCanceledException();
        _pending.Enqueue((sheet, Task.Run(() =>
        {
            var processor = new ManagedDwgProcessor();
            try { return processor.Prepare(request, input, () => _stop.IsCancellationRequested); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (input != fallback)
            {
                var baseline = new BridgeRequest { Operation = "Flatten", RevitSheet = true, UseLayerColors = request.UseLayerColors, LayerStyles = request.LayerStyles,
                    WideLineLayers = request.WideLineLayers, FamilySources = request.FamilySources, ExcludedLayers = request.ExcludedLayers };
                var result = processor.Prepare(baseline, fallback, () => _stop.IsCancellationRequested);
                result.Response.Warnings.Add($"필터 출력 실패: {ex.Message} · 기본 카테고리 DWG로 저장을 계속했습니다.");
                return result;
            }
        }, _stop.Token)));
    }

    public IReadOnlyList<(string Sheet, PreparedDrawing Drawing)> Finish()
    { while (_pending.Count > 0) CompleteNext(); return _ready; }

    private void CompleteNext()
    {
        var entry = _pending.Peek();
        while (!entry.Work.IsCompleted)
        {
            _pump();
            if (_cancel()) { _stop.Cancel(); throw new OperationCanceledException(); }
            // A bounded wait avoids busy polling without starving the Revit UI.
            ((IAsyncResult)entry.Work).AsyncWaitHandle.WaitOne(50);
        }
        var drawing = entry.Work.GetAwaiter().GetResult();
        _pending.Dequeue(); _ready.Add((entry.Sheet, drawing));
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var entry in _pending)
        {
            while (!entry.Work.IsCompleted) { _pump(); ((IAsyncResult)entry.Work).AsyncWaitHandle.WaitOne(50); }
            try { entry.Work.GetAwaiter().GetResult(); } catch { /* caller retains the first failure/cancel */ }
        }
        _stop.Dispose();
    }
}
