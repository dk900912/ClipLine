namespace ClipLine.Domain;

public sealed record HangOutcome(Pegged? Added, Pegged? Evicted, bool Duplicate, bool NoLooseVictim);

/// <summary>
/// 绳的数据模型，直译 Swift 的 Line.swift。不引用任何 Windows API。
/// 规则来源：V-31…V-37、D-01…D-11、F-09、F-11、G-17、X-02。
/// </summary>
public sealed class Line
{
    private readonly List<Pegged> _items = new();

    public Line(int screenWidthDip = 1920, CardSize size = CardSize.Medium, int keepOnLine = 0)
    {
        Size = size;
        KeepOnLine = keepOnLine;
        Fits = Layout.VisibleCount(screenWidthDip, size);
        MaxItems = Layout.MaxItems(Fits, keepOnLine);
    }

    public IReadOnlyList<Pegged> Items => _items;
    public CardSize Size { get; private set; }
    public int Fits { get; private set; }
    public int MaxItems { get; private set; }
    public int KeepOnLine { get; private set; }
    public int Scroll { get; private set; }
    public UndoSteps Undo { get; } = new();

    public int LiveCount => _items.Count(i => i.IsLive);
    public int KeptCount => _items.Count(i => i.Kept && i.IsLive);
    public int HeldCount => _items.Count(i => i.Held);
    public bool IsEmpty => LiveCount == 0;

    /// <summary>V-04 / D-09：屏幕参数或尺寸变化后重算容量，并按新容量淘汰。</summary>
    public void Configure(int screenWidthDip, CardSize size, int keepOnLine)
    {
        Size = size;
        KeepOnLine = keepOnLine;
        Fits = Layout.VisibleCount(screenWidthDip, size);
        MaxItems = Layout.MaxItems(Fits, keepOnLine);
        TrimToCapacity();
        ClampScroll();
    }

    /// <summary>V-32：调低「绳上保留」时循环淘汰最旧的普通照片。</summary>
    public void SetKeep(int keepOnLine)
    {
        KeepOnLine = keepOnLine;
        MaxItems = Layout.MaxItems(Fits, keepOnLine);
        TrimToCapacity();
        ClampScroll();
    }

    private void TrimToCapacity() => TrimToCapacity(null);

    /// <summary>
    /// 淘汰到容量内。neverDrop 用于 D-06：放回后满绳淘汰旧照片，但不淘汰刚恢复的。
    /// </summary>
    public void TrimToCapacity(ISet<Guid>? neverDrop)
    {
        while (LiveCount > MaxItems)
        {
            var victim = OldestLoose(neverDrop);
            if (victim is null) break; // 固定照片永不自动掉落
            victim.Falling = true;
        }
    }

    /// <summary>V-33：第一个非掉落、非固定、非拿在手上、非手动挂上的照片。</summary>
    public Pegged? OldestLoose(ISet<Guid>? neverDrop = null) =>
        _items.FirstOrDefault(i => i.IsLoose && (neverDrop is null || !neverDrop.Contains(i.Id)));

    public static string NormalizePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar).ToLowerInvariant();
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    /// <summary>F-09：同一路径已在绳上就不再挂；多个 watcher 报告同一文件只挂一次。</summary>
    public bool ContainsLivePath(string path)
    {
        string normalized = NormalizePath(path);
        return _items.Any(i => i.IsLive && NormalizePath(i.Path) == normalized);
    }

    /// <summary>挂上一张照片。固定项插到绳结左侧（D-02），满绳一次只挤掉 1 张（V-33）。</summary>
    public HangOutcome Hang(Pegged item)
    {
        if (ContainsLivePath(item.Path)) return new HangOutcome(null, null, true, false);

        if (item.Kept || item.ByHand)
        {
            _items.Insert(KeptCount, item);
        }
        else
        {
            _items.Add(item);
        }

        Scroll = 0; // V-34

        if (LiveCount <= MaxItems) return new HangOutcome(item, null, false, false);

        var victim = OldestLoose();
        if (victim is null) return new HangOutcome(item, null, false, true); // 全是固定项
        victim.Falling = true;
        return new HangOutcome(item, victim, false, false);
    }

    /// <summary>D-09：恢复时先按默认容量接纳全部，再按屏宽重算，避免每次启动都掉图。</summary>
    public void RestoreFrom(IEnumerable<Pegged> items, Settings settings, int screenWidthDip)
    {
        _items.Clear();
        Undo.Clear();
        Scroll = 0;
        MaxItems = int.MaxValue;
        foreach (var item in items)
        {
            if (!ContainsLivePath(item.Path)) _items.Add(item);
        }
        Configure(screenWidthDip, settings.LineSize, settings.KeepOnLine);
    }

    /// <summary>F-11：外部删除/移动文件时对应卡片自动掉落。</summary>
    public bool RemoveByPath(string path)
    {
        string normalized = NormalizePath(path);
        var hit = _items.FirstOrDefault(i => i.IsLive && NormalizePath(i.Path) == normalized);
        if (hit is null) return false;
        hit.Falling = true;
        return true;
    }

    public Pegged? Find(Guid id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>V-37：丢弃后保留空位，直到 0.6 秒后且指针离开才闭合空隙。</summary>
    public int CloseGaps()
    {
        int before = _items.Count;
        _items.RemoveAll(i => i.Falling);
        ClampScroll();
        return before - _items.Count;
    }

    /// <summary>D-02 / D-03：固定移到绳结左侧末尾；取消固定后回到普通区最前。</summary>
    public bool SetPinned(Guid id, bool pinned)
    {
        var item = Find(id);
        if (item is null || item.Falling) return false;
        _items.Remove(item);
        item.Kept = pinned;
        // 固定项落在绳结左侧末尾；取消固定后也回到普通区最前（两者此刻索引相同）。
        int index = Math.Clamp(KeptCount, 0, _items.Count);
        _items.Insert(index, item);
        return true;
    }

    /// <summary>D-04：拿在手上不是副本，绳上留淡影；放回时清标记。</summary>
    public bool SetHeld(Guid id, bool held)
    {
        var item = Find(id);
        if (item is null) return false;
        item.Held = held;
        return true;
    }

    /// <summary>V-16 / G-05 / D-03 / D-07：角标语义。</summary>
    public static CornerAction CornerActionFor(Pegged item)
    {
        if (item.Kept && !item.ByHand) return CornerAction.Unpin;
        if (item.ByHand) return CornerAction.Hide;
        return item.InOwnDirectory ? CornerAction.Trash : CornerAction.Hide;
    }

    /// <summary>V-35：滚轮/触控板，非精确滚动 ×12，结果夹在 [0, scrollMax]。</summary>
    public int ScrollBy(double delta, bool precise, int screenWidthDip)
    {
        double factor = precise ? 1.0 : 12.0;
        double max = Layout.ScrollMax(_items.Count(i => i.IsLive), Fits, Layout.Spacing(Size));
        Scroll = (int)Math.Clamp(Scroll + delta * factor, 0, max);
        return Scroll;
    }

    private void ClampScroll()
    {
        double max = Layout.ScrollMax(_items.Count(i => i.IsLive), Fits, Layout.Spacing(Size));
        Scroll = (int)Math.Clamp(Scroll, 0, max);
    }

    /// <summary>取出一组条目（批量算一步撤销）。takeAll=true 对应「全部取下」。</summary>
    public TakenDownBatch TakeDown(IEnumerable<Guid> ids, long nowMs)
    {
        var wanted = ids.ToHashSet();
        var batch = new TakenDownBatch { AtMs = nowMs };
        for (int index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (!item.IsLive || item.Held || !wanted.Contains(item.Id)) continue;
            item.Falling = true;
            batch.Entries.Add(new TakenDownEntry
            {
                Index = index,
                ItemId = item.Id,
                Path = item.Path,
                Item = item,
                WasKept = item.Kept,
                WasByHand = item.ByHand,
                WasHeld = item.Held,
            });
        }
        if (batch.Entries.Count > 0) Undo.Push(batch);
        return batch;
    }

    public TakenDownBatch TakeDownAll(long nowMs) => TakeDown(_items.Select(i => i.Id), nowMs);

    /// <summary>G-17：取下某一侧的全部非固定、非拿在手上邻居。</summary>
    public IEnumerable<Guid> NeighbourIds(Guid id, bool left)
    {
        var item = Find(id);
        if (item is null) return Array.Empty<Guid>();
        int index = _items.IndexOf(item);
        return _items
            .Where((candidate, i) => left ? i < index : i > index)
            .Where(candidate => candidate.IsLoose)
            .Select(candidate => candidate.Id)
            .ToArray();
    }

    /// <summary>D-06：倒序恢复到原索引 / 原空位；刚恢复的不参与淘汰。</summary>
    public List<Pegged> PutBack(TakenDownBatch batch)
    {
        var restored = new List<Pegged>();
        foreach (var entry in batch.Entries.OrderByDescending(e => e.Index))
        {
            var existing = Find(entry.ItemId);
            if (existing is not null)
            {
                existing.Falling = false;
                restored.Add(existing);
                continue;
            }
            if (entry.Item is null) continue;
            entry.Item.Falling = false;
            _items.Insert(Math.Clamp(entry.Index, 0, _items.Count), entry.Item);
            restored.Add(entry.Item);
        }
        ClampScroll();
        return restored;
    }
}
