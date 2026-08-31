using ChangExport.DwgProcessing;
using ChangExport.Export;

internal static class PreparationQueueRegression
{
    public static void Run(string nativeRoot, Action<bool, string> check)
    {
        int thread = Environment.CurrentManagedThreadId;
        string first = Path.Combine(nativeRoot, "native_001", "sheet.dwg"), second = Path.Combine(nativeRoot, "native_002", "sheet.dwg");
        using (var queue = new DwgPreparationQueue(() => false, () =>
            { if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("UI callback from worker"); }))
        {
            queue.Enqueue("first", new BridgeRequest { RevitSheet = true }, first, first);
            queue.Enqueue("second", new BridgeRequest { RevitSheet = true }, second, second);
            var ready = queue.Finish();
            check(ready.Select(r => r.Sheet).SequenceEqual(new[] { "first", "second" }), "Parallel completion retains sheet order");
            check(ready[0].Drawing.Response.ModelEntityCount == 15580 && ready[1].Drawing.Response.ModelEntityCount == 812, "Parallel jobs retain actual sheet contents");
        }
        using (var queue = new DwgPreparationQueue(() => false, () => { }))
        {
            queue.Enqueue("fallback", new BridgeRequest { RevitSheet = true }, first + ".missing", first);
            var ready = queue.Finish();
            check(ready[0].Drawing.Response.Warnings.Any(w => w.Contains("필터 출력 실패")) && ready[0].Drawing.Response.ModelEntityCount == 15580,
                "Filtered DWG failure falls back to the intact native drawing");
        }
        bool cancel = false;
        using (var queue = new DwgPreparationQueue(() => cancel, () => { }))
        {
            queue.Enqueue("cancel", new BridgeRequest { RevitSheet = true }, first, first); cancel = true;
            try { queue.Finish(); check(false, "Cancel observed while worker is running"); }
            catch (OperationCanceledException) { check(true, "Cancel observed while worker is running"); }
        }
        using (var queue = new DwgPreparationQueue(() => false, () => { }))
        {
            queue.Enqueue("bad", new BridgeRequest { RevitSheet = true }, first + ".missing", first + ".missing");
            queue.Enqueue("other", new BridgeRequest { RevitSheet = true }, second, second);
            try { queue.Finish(); check(false, "Failure surfaced before final save"); }
            catch (IOException) { check(true, "Failure surfaced before final save"); }
        }
    }
}
