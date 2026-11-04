namespace ClipLine.Domain;

/// <summary>一次批量动作中的一项：记住原索引与原文件位置，供倒序恢复（D-06）。</summary>
public sealed class TakenDownEntry
{
    public required int Index { get; init; }
    public required Guid ItemId { get; init; }
    public required string Path { get; init; }
    public string? TrashedPath { get; set; }

    /// <summary>内存中的原始对象；CloseGaps 之后仍可倒序插回原空位（D-06）。</summary>
    public Pegged? Item { get; init; }

    public bool WasKept { get; init; }
    public bool WasByHand { get; init; }
    public bool WasHeld { get; init; }
}

/// <summary>一次批量动作算一步（D-06）。</summary>
public sealed class TakenDownBatch
{
    public long AtMs { get; init; }
    public List<TakenDownEntry> Entries { get; } = new();
}

/// <summary>只在内存中保存，最多 20 步（D-06）。</summary>
public sealed class UndoSteps
{
    public const int MaxSteps = 20;
    private readonly List<TakenDownBatch> _steps = new();

    public int Count => _steps.Count;
    public bool CanUndo => _steps.Count > 0;

    public void Push(TakenDownBatch batch)
    {
        if (_steps.Count == MaxSteps) _steps.RemoveAt(0);
        _steps.Add(batch);
    }

    public TakenDownBatch? Pop()
    {
        if (_steps.Count == 0) return null;
        int last = _steps.Count - 1;
        var batch = _steps[last];
        _steps.RemoveAt(last);
        return batch;
    }

    public void Clear() => _steps.Clear();
}
