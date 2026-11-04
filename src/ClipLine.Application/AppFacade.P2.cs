using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>P2：三层捕获链路接入同一个应用门面，不新增测试接缝。</summary>
public sealed partial class AppFacade
{
    private readonly IntegrityTracker _integrity = new();
    private readonly ClipboardDedupe _clipboardDedupe = new();
    private readonly Dictionary<string, long> _lastFileEventMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _startedAtMs = 0;
    private int _clipboardOrdinal;

    private void OnFileCreated(FileCreated e)
    {
        var candidate = new CaptureCandidate(
            e.Path, e.Source, e.CreatedAtMs, e.HasScreenCaptureMark,
            e.Source == CaptureSource.Desktop);
        if (!CaptureRules.Accept(candidate, _startedAtMs)) return;
        if (_line.ContainsLivePath(e.Path)) return; // F-09

        // F-05：0.2 秒防抖，多个 watcher 同时报告同一文件只处理一次。
        if (_lastFileEventMs.TryGetValue(e.Path, out long last) &&
            _nowMs - last < CaptureRules.DebounceMs) return;
        _lastFileEventMs[e.Path] = _nowMs;

        if (!e.IsComplete)
        {
            _integrity.Track(e.Path, _nowMs, e.Size);
            Emit(CommandKind.StartIntegrityRetry, e.Path, CaptureIntegrity.RetryIntervalMs);
            Schedule(CaptureIntegrity.RetryIntervalMs, new IntegrityRetryElapsed(e.Path));
            return;
        }

        Dispatch(new NewItem(
            e.Path,
            CaptureRules.ToOrigin(e.Source),
            CaptureIntegrity.IsVideo(e.Path),
            false,
            e.Source is CaptureSource.OwnScreenshots or CaptureSource.OwnCaptures));
    }

    private void OnIntegrityRetry(IntegrityRetryElapsed e)
    {
        var decision = _integrity.Tick(e.Path, _nowMs);
        if (decision.GiveUp)
        {
            Emit(CommandKind.StopIntegrityRetry, e.Path, decision.Attempts);
            return;
        }
        if (!decision.Retry) return;
        long stableForMs = _integrity.StableForMs(e.Path, _nowMs);
        Emit(CommandKind.StartIntegrityRetry, e.Path, stableForMs);
        Schedule(CaptureIntegrity.RetryIntervalMs, new IntegrityRetryElapsed(e.Path));
    }

    private void OnIntegrityResolved(FileIntegrityResolved e)
    {
        if (!e.Complete || !_integrity.IsTracked(e.Path)) return;
        _integrity.Resolve(e.Path);
        Emit(CommandKind.StopIntegrityRetry, e.Path);
        // 完整性等待只发生在监视文件夹链路上；系统整体落盘的截图走即时路径（R-01）。
        Dispatch(new NewItem(
            e.Path,
            ItemOrigin.WatchedFolder,
            CaptureIntegrity.IsVideo(e.Path),
            false,
            true));
    }

    private void OnClipboardChanged(ClipboardChanged e)
    {
        if (!ClipboardFilter.Accept(e.Snapshot, _settings.ClipboardEnabled, out _)) return;

        string fileName;
        string path;
        int guard = 0;
        do
        {
            fileName = ClipboardFilter.FileName(e.Timestamp, _clipboardOrdinal++);
            path = System.IO.Path.Combine(_env.OwnDirectory, fileName);
        }
        while (_env.FileExists(path) && guard++ < 100);

        if (_clipboardDedupe.IsDuplicate(e.Snapshot.Hash, p => _env.FileExists(p))) return;
        _clipboardDedupe.Remember(e.Snapshot.Hash, path);

        Emit(CommandKind.SaveCopiedPng, path, ClipboardFilter.FileModeOwnerOnly);
        Dispatch(new NewItem(path, ItemOrigin.Clipboard, false, false, true));
    }
}
