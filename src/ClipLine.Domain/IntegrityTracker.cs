namespace ClipLine.Domain;

/// <summary>等待写完整的文件；600 秒后从表中移除，保证不泄漏内存（R-02）。</summary>
public sealed class PendingFile
{
    public required string Path { get; init; }
    public long FirstSeenMs { get; init; }
    public long LastSize { get; set; }
    public long SizeChangedAtMs { get; set; }
    public int Attempts { get; set; }
}

public sealed record RetryDecision(bool Retry, bool GiveUp, int Attempts);

/// <summary>纯内存的完整性复查队列，由平台层用 ReadDirectoryChangesW 的 MODIFIED 事件喂数据。</summary>
public sealed class IntegrityTracker
{
    private readonly Dictionary<string, PendingFile> _pending = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _pending.Count;

    public IReadOnlyCollection<PendingFile> Pending => _pending.Values;

    public PendingFile Track(string path, long nowMs, long size)
    {
        if (_pending.TryGetValue(path, out var existing)) return existing;
        var file = new PendingFile
        {
            Path = path,
            FirstSeenMs = nowMs,
            LastSize = size,
            SizeChangedAtMs = nowMs,
        };
        _pending[path] = file;
        return file;
    }

    /// <summary>文件大小变化 → 重新计算「0.5 秒内大小不变」窗口。</summary>
    public void ReportSize(string path, long size, long nowMs)
    {
        if (!_pending.TryGetValue(path, out var file)) return;
        if (file.LastSize != size)
        {
            file.LastSize = size;
            file.SizeChangedAtMs = nowMs;
        }
    }

    public long StableForMs(string path, long nowMs) =>
        _pending.TryGetValue(path, out var file) ? nowMs - file.SizeChangedAtMs : 0;

    public RetryDecision Tick(string path, long nowMs)
    {
        if (!_pending.TryGetValue(path, out var file)) return new RetryDecision(false, false, 0);
        file.Attempts++;
        if (nowMs - file.FirstSeenMs >= CaptureIntegrity.GiveUpAfterMs)
        {
            _pending.Remove(path);
            return new RetryDecision(false, true, file.Attempts);
        }
        return new RetryDecision(true, false, file.Attempts);
    }

    public bool Resolve(string path) => _pending.Remove(path);

    public bool IsTracked(string path) => _pending.ContainsKey(path);
}
