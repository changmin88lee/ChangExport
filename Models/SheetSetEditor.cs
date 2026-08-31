namespace ChangExport.Models;

public sealed class SheetSetEditor
{
    public List<SheetSetDefinition> Sets { get; }
    public HashSet<string> SelectedIds { get; } = new();
    private string? _anchor;
    public SheetSetEditor(IEnumerable<SheetSetDefinition> sets) => Sets = sets.Select(s => s.Copy()).ToList();

    public void Select(string id, bool control, bool shift)
    {
        int current = Sets.FindIndex(s => s.Id == id), anchor = Sets.FindIndex(s => s.Id == _anchor);
        if (current < 0) return;
        if (shift && anchor >= 0)
        {
            if (!control) SelectedIds.Clear();
            for (int i = Math.Min(current, anchor); i <= Math.Max(current, anchor); i++) SelectedIds.Add(Sets[i].Id);
            return;
        }
        if (!control) SelectedIds.Clear();
        if (!SelectedIds.Add(id) && control) SelectedIds.Remove(id);
        _anchor = id;
    }

    public void Combine(string name)
    {
        var selected = Sets.Where(s => SelectedIds.Contains(s.Id)).ToList();
        if (selected.Count < 2) throw new InvalidOperationException("시트 또는 세트를 두 개 이상 선택하세요.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("세트 이름을 입력하세요.");
        int index = Sets.IndexOf(selected[0]);
        var combined = new SheetSetDefinition { Name = name.Trim(), Direction = selected[0].Direction, MarginMm = selected[0].MarginMm,
            SheetUniqueIds = selected.SelectMany(s => s.SheetUniqueIds).ToList() };
        if (combined.SheetUniqueIds.Distinct().Count() != combined.SheetUniqueIds.Count) throw new InvalidOperationException("중복 시트가 있습니다.");
        Sets.RemoveAll(s => SelectedIds.Contains(s.Id)); Sets.Insert(index, combined);
        SelectedIds.Clear(); SelectedIds.Add(combined.Id); _anchor = combined.Id;
    }

    public void Release(IReadOnlyDictionary<string, SheetDescriptor> sheets)
    {
        if (SelectedIds.Count != 1) throw new InvalidOperationException("해제할 세트 하나를 선택하세요.");
        int index = Sets.FindIndex(s => SelectedIds.Contains(s.Id));
        if (index < 0 || Sets[index].SheetUniqueIds.Count < 2) return;
        var previous = Sets[index]; Sets.RemoveAt(index); SelectedIds.Clear();
        foreach (string id in previous.SheetUniqueIds)
        {
            var single = new SheetSetDefinition { Name = sheets.TryGetValue(id, out var sheet) ? sheet.Number : "없는 시트",
                SheetUniqueIds = new() { id }, Direction = previous.Direction, MarginMm = previous.MarginMm };
            Sets.Insert(index++, single); SelectedIds.Add(single.Id);
        }
        _anchor = null;
    }

    public void MoveSheet(string setId, int index, int offset)
    {
        var members = Sets.Single(s => s.Id == setId).SheetUniqueIds;
        int target = index + offset;
        if (index < 0 || target < 0 || index >= members.Count || target >= members.Count) return;
        (members[index], members[target]) = (members[target], members[index]);
    }
}
