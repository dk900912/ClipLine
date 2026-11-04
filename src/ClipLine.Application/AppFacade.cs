using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>
/// 应用门面 —— v1 唯一的测试接缝（Issue #1 / #3 定稿）。
/// 离散事件进，状态快照 + 外部命令出；不直接调用任何 Win32 API，不持有真实时钟。
/// 所有时序由 Schedule + AdvanceTo 驱动，平台能力全部由 RecordingCommandSink 冒充。
/// 后续阶段通过同名的 partial 文件扩展输入处理，接缝数量始终为 1。
/// </summary>
public sealed partial class AppFacade
{
    private sealed record ScheduledInput(long DueMs, long Seq, AppInput Input);

    private readonly IClock _clock;
    private readonly IEnvironment _env;
    private readonly ICommandSink? _sink;
    private readonly Line _line;
    private readonly string _initialJson;
    private readonly List<AppCommand> _pending = new();
    private readonly List<ScheduledInput> _scheduled = new();
    private readonly Dictionary<Guid, bool> _copiedBadges = new();
    private Settings _settings = new();

    private long _nowMs;
    private long _seq;
    private long _peekUntilMs;
    private long _awaySinceMs;
    private long _hotZoneSinceMs;
    private long _lastDiscardMs;
    private long _hotZoneToken;
    private long _retractToken;
    private long _peekToken;
    private long _destroyToken;
    private long _gapToken;
    private int _activeFlights;

    private bool _isPresent;
    private bool _isRevealed;
    private bool _wanted;
    private bool _blocked;
    private bool _pinned;
    private bool _keepOpen;
    private bool _openedOnPurpose;
    private bool _menuBarSuppressed;
    private bool _isShowingMenu;
    private bool _isDragging;
    private bool _cardPressed;
    private bool _pointerInHotZone;
    private bool _pointerInLineArea;
    private bool _pointerWasInside;
    private bool _welcomePinned;
    private bool _borrowedCtrlZ;
    private TrayState _tray = TrayState.Dimmed;

    public AppFacade(AppDependencies dependencies)
    {
        _clock = dependencies.Clock;
        _env = dependencies.Environment;
        _sink = dependencies.Sink;
        _initialJson = dependencies.PersistedJson ?? string.Empty;
        _line = new Line(_env.ScreenWidthDip);
    }

    public StateSnapshot Current => Snapshot();

    /// <summary>送入一个离散事件，返回该事件产生的状态快照与全部外部命令。</summary>
    public AppResult Handle(AppInput input)
    {
        _pending.Clear();
        _nowMs = Math.Max(_nowMs, _clock.NowMs);
        Dispatch(input);
        return Complete();
    }

    /// <summary>假时钟推进：按到期时间与序号处理全部内部调度事件。</summary>
    public AppResult AdvanceTo(long nowMs)
    {
        _pending.Clear();
        _nowMs = Math.Max(_nowMs, nowMs);
        int guard = 0;
        while (guard++ < 10_000)
        {
            ScheduledInput? due = _scheduled
                .Where(s => s.DueMs <= _nowMs)
                .OrderBy(s => s.DueMs)
                .ThenBy(s => s.Seq)
                .FirstOrDefault();
            if (due is null) break;
            _scheduled.Remove(due);
            Dispatch(due.Input);
        }
        return Complete();
    }

    public AppResult AdvanceBy(long deltaMs) => AdvanceTo(_nowMs + deltaMs);

    private AppResult Complete()
    {
        var commands = _pending.ToArray();
        if (_sink is not null)
        {
            foreach (var command in commands) _sink.Execute(command);
        }
        return new AppResult(Snapshot(), commands);
    }

    private void Emit(CommandKind kind, string? argument = null, double number = 0) =>
        _pending.Add(new AppCommand(kind, argument, number));

    private void Schedule(long delayMs, AppInput input) =>
        _scheduled.Add(new ScheduledInput(_nowMs + Math.Max(0, delayMs), ++_seq, input));

    private void Persist() =>
        Emit(CommandKind.PersistState, StateStore.Serialize(StateStore.Capture(_line, _settings)));

    private void UpdateTray()
    {
        var next = _line.IsEmpty
            ? TrayState.Dimmed
            : _settings.ClipboardEnabled ? TrayState.Monitoring : TrayState.Default;
        if (next != _tray)
        {
            _tray = next;
            Emit(CommandKind.SetTrayIcon, next.ToString());
        }
        string first = _settings.ClipboardEnabled ? "ClipLine，正在挂上复制的图片" : "ClipLine";
        string second = _line.IsEmpty ? "晾衣绳上是空的" : $"晾衣绳上有 {_line.LiveCount} 张";
        Emit(CommandKind.SetTrayTooltip, first + "|" + second);
    }

    private void Reveal(bool pin)
    {
        if (_blocked && !pin) return;
        if (!_isPresent)
        {
            Emit(CommandKind.PresentWindow);
            _isPresent = true;
        }
        _wanted = true;
        if (_isRevealed) return;
        _isRevealed = true;
        Emit(CommandKind.SetHitTest, "cards");
    }

    private void Hide()
    {
        _wanted = false;
        _pinned = false;
        _keepOpen = false;
        _openedOnPurpose = false;
        _peekUntilMs = 0;
        if (!_isRevealed) return; // 已经收起时不重复发命令、不重复排销毁
        _isRevealed = false;
        Emit(CommandKind.SetHitTest, "passthrough");
        _destroyToken = ++_seq;
        Schedule(Layout.DestroyDelayMs, new DestroyElapsed(_destroyToken));
    }

    private void SetPeek(long durationMs)
    {
        _peekUntilMs = _nowMs + durationMs;
        _peekToken = ++_seq;
        Schedule(durationMs, new PeekExpired(_peekToken));
    }

    private bool CanHide() =>
        CanHideIgnoringPeek() && _nowMs >= _peekUntilMs;

    /// <summary>S-08：空绳 0.7 秒隐藏优先于仍在进行的 peek。</summary>
    private bool CanHideIgnoringPeek() =>
        !_isDragging && !_isShowingMenu && !_cardPressed && !_keepOpen && !_pinned && !_welcomePinned &&
        !_pointerInHotZone && !_pointerInLineArea;

    /// <summary>被 S-05 的条件挡住时，保留 token 重试，条件解除后仍能收起。</summary>
    private void RetryRetract(long token) => Schedule(Layout.RetractDelayMs, new RetractElapsed(token));

    private void ScheduleEmptyHide()
    {
        long token = ++_seq;
        Schedule(Layout.EmptyHideMs, new EmptyHideElapsed(token));
    }

    private StateSnapshot Snapshot() => new()
    {
        NowMs = _nowMs,
        IsPresent = _isPresent,
        IsRevealed = _isRevealed,
        Wanted = _wanted,
        Blocked = _blocked,
        Pinned = _pinned,
        PeekActive = _nowMs < _peekUntilMs,
        MenuBarSuppressed = _menuBarSuppressed,
        OpenedOnPurpose = _openedOnPurpose,
        KeepOpen = _keepOpen,
        IsShowingMenu = _isShowingMenu,
        IsDragging = _isDragging,
        CardPressed = _cardPressed,
        LiveCount = _line.LiveCount,
        VisibleCount = _line.Fits,
        MaxItems = _line.MaxItems,
        Scroll = _line.Scroll,
        UndoCount = _line.Undo.Count,
        KeepOnLine = _settings.KeepOnLine,
        Size = _settings.LineSize,
        ClipboardEnabled = _settings.ClipboardEnabled,
        SoundOn = _settings.SoundOn,
        InboxEnabled = _settings.InboxEnabled,
        RevealOnPointer = _settings.RevealOnPointer,
        PeekOnNewContent = _settings.PeekOnNewContent,
        ShowOverFullScreen = _settings.ShowOverFullScreen,
        LaunchAtLogin = _settings.LaunchAtLogin,
        Shortcut = _settings.Shortcut,
        Language = _settings.Language,
        ExtraFolders = _settings.ExtraFolders.ToArray(),
        Items = _line.Items.Select(i => new ItemSnapshot(
            i.Id, i.Path, i.Kept, i.Held, i.ByHand, i.Falling, i.IsRecording,
            i.Tilt, Line.CornerActionFor(i), _copiedBadges.ContainsKey(i.Id))).ToArray(),
        Tray = _tray,
        ReduceMotion = _env.ReduceMotion,
        DarkTheme = _env.IsDarkTheme,
        HighContrast = _env.IsHighContrast,
        ActiveFlights = _activeFlights,
        WelcomePinned = _welcomePinned,
    };

    private void Dispatch(AppInput input)
    {
        switch (input)
        {
            case ApplicationStarted e: OnStarted(e); break;
            case ApplicationExiting: OnExiting(); break;
            case PointerMoved e: OnPointerMoved(e); break;
            case TopBandClicked e: OnTopBandClicked(e); break;
            case ToggleLine: OnToggleLine(); break;
            case MenuToggled e: _isShowingMenu = e.Open; break;
            case DragStateChanged e: _isDragging = e.Dragging; break;
            case CardPressedChanged e: _cardPressed = e.Pressed; break;
            case NewItem e: OnNewItem(e); break;
            case TakeDownAll: OnTakeDown(_line.TakeDownAll(_nowMs)); break;
            case TakeDownIds e: OnTakeDown(_line.TakeDown(e.Ids, _nowMs)); break;
            case PutBack: OnPutBack(); break;
            case HangEverythingBack: OnHangEverythingBack(); break;
            case Scroll e: _line.ScrollBy(e.Delta, e.Precise, _env.ScreenWidthDip); break;
            case FileDisappeared e: OnFileDisappeared(e); break;
            case FullScreenChanged e: OnFullScreenChanged(e); break;
            case SetClipboardEnabled e: OnSetClipboardEnabled(e); break;
            case SetKeepOnLine e: OnSetKeep(e); break;
            case SetSize e: OnSetSize(e); break;
            case SetSound e: _settings.SoundOn = e.On; Persist(); break;
            case SetRevealOnPointer e: _settings.RevealOnPointer = e.On; Persist(); break;
            case SetPeekOnNewContent e: _settings.PeekOnNewContent = e.On; Persist(); break;
            case SetShowOverFullScreen e: OnSetShowOverFullScreen(e); break;
            case SetInboxEnabled e: OnSetInboxEnabled(e); break;
            case SetShortcut e: OnSetShortcut(e); break;
            case SetLaunchAtLogin e: OnSetLaunchAtLogin(e); break;
            case AddWatchedFolder e: OnAddWatchedFolder(e); break;
            case RemoveWatchedFolder e: OnRemoveWatchedFolder(e); break;
            case OpenScreenshotsFolder: Emit(CommandKind.OpenFolder, _env.ScreenshotsFolder); break;
            case CheckForUpdates: Emit(CommandKind.OpenBrowser, $"v={_env.Version}&lang={_settings.Language}"); break;

            case HotZoneElapsed e: OnHotZoneElapsed(e); break;
            case RetractElapsed e:
                if (e.Token != _retractToken) break;
                if (CanHide()) Hide(); else if (_awaySinceMs > 0) RetryRetract(e.Token);
                break;
            case PeekExpired e:
                if (e.Token != _peekToken) break;
                if (CanHide()) Hide(); else if (_nowMs >= _peekUntilMs) Schedule(250, new PeekExpired(e.Token));
                break;
            case EmptyHideElapsed:
                if (_line.IsEmpty && !_openedOnPurpose && CanHideIgnoringPeek()) Hide();
                break;
            case DestroyElapsed e: OnDestroyElapsed(e); break;
            case GapCloseElapsed e: OnGapCloseElapsed(e); break;
            case FullScreenRecheckElapsed: OnFullScreenRecheck(); break;
            case WelcomeElapsed: OnWelcomeElapsed(); break;
            case InboxOfferElapsed: OnInboxOfferElapsed(); break;
            case BatchStepElapsed e: Emit(CommandKind.StartFall, e.Path, Layout.FallMs); break;
            case CopiedBadgeElapsed e: _copiedBadges.Remove(e.ItemId); break;
            case FlightFinished: _activeFlights = Math.Max(0, _activeFlights - 1); break;

            default: break; // 后续阶段通过 partial 扩展点 DispatchExtension 处理
        }
        DispatchExtension(input);
    }

    /// <summary>后续阶段（P2–P5）在此挂接新增输入，接缝仍只有应用门面一个。</summary>
    partial void DispatchExtension(AppInput input);

    private void OnStarted(ApplicationStarted e)
    {
        string json = string.IsNullOrEmpty(e.PersistedJson) ? _initialJson : e.PersistedJson;
        var plan = StateStore.Restore(
            json,
            path => _env.FileExists(path) && _env.CanDecode(path),
            _env.ScreenWidthDip);
        _settings = plan.Settings;
        _line.RestoreFrom(plan.Items, _settings, _env.ScreenWidthDip);

        Emit(CommandKind.RegisterHotKey, _settings.Shortcut);
        if (_settings.ClipboardEnabled) Emit(CommandKind.RegisterClipboardListener);
        if (_settings.InboxEnabled) Emit(CommandKind.WatchFolder, _env.ScreenshotsFolder);
        foreach (string folder in _settings.ExtraFolders) Emit(CommandKind.WatchFolder, folder);
        if (_settings.LaunchAtLogin) Emit(CommandKind.SetLaunchAtLogin, "true");
        UpdateTray();

        if (_line.LiveCount > 0)
        {
            Reveal(false);
            SetPeek(Layout.PeekMs);
        }

        if (e.FreshInstall || !_settings.Welcomed)
        {
            _settings.Welcomed = true;
            _welcomePinned = true;
            Emit(CommandKind.ShowWelcome);
            Reveal(true);
            Schedule(Layout.WelcomePinMs, new WelcomeElapsed());
            Persist();
        }

        if (!_settings.InboxOffered) Schedule(Layout.InboxOfferDelayMs, new InboxOfferElapsed());
    }

    private void OnExiting()
    {
        Emit(CommandKind.UnregisterHotKey);
        Emit(CommandKind.UnregisterClipboardListener);
        Emit(CommandKind.RestoreSettings);
        Emit(CommandKind.DestroyWindow);
        _isPresent = false;
        _isRevealed = false;
        Persist();
    }

    private void OnPointerMoved(PointerMoved e)
    {
        int width = Math.Max(1, _env.ScreenWidthDip);
        bool inBand = e.Y >= 0 && e.Y <= Layout.RevealBandDip &&
                      e.X >= width / 3.0 && e.X <= 2.0 * width / 3.0;
        bool inLineArea = _isRevealed && e.Y >= 0 &&
                          e.Y <= Layout.PanelHeight(_settings.LineSize) + Layout.Sag(width);

        _pointerInHotZone = inBand && !_menuBarSuppressed;
        _pointerInLineArea = inLineArea;
        if (inLineArea) _pointerWasInside = true;
        if (_menuBarSuppressed && e.Y > Layout.RevealBandDip) _menuBarSuppressed = false;

        if (_pointerInHotZone)
        {
            if (_hotZoneSinceMs == 0)
            {
                _hotZoneSinceMs = _nowMs;
                _hotZoneToken = ++_seq;
                Schedule(Layout.RevealDelayMs, new HotZoneElapsed(_hotZoneToken));
            }
        }
        else
        {
            _hotZoneSinceMs = 0;
            _hotZoneToken = 0;
        }

        if (!_pointerInHotZone && !inLineArea)
        {
            if (_awaySinceMs == 0)
            {
                _awaySinceMs = _nowMs;
                _retractToken = ++_seq;
                Schedule(Layout.RetractDelayMs, new RetractElapsed(_retractToken));
            }
            if (_openedOnPurpose && _pointerWasInside)
            {
                _pinned = false;
                _keepOpen = false;
                _openedOnPurpose = false;
            }
        }
        else
        {
            _awaySinceMs = 0;
            _retractToken = 0;
        }

        // K-02：只在线内且有可撤销步骤时临时借用 Ctrl+Z，离开即注销。
        bool borrow = inLineArea && _line.Undo.CanUndo;
        if (borrow != _borrowedCtrlZ)
        {
            _borrowedCtrlZ = borrow;
            Emit(borrow ? CommandKind.RegisterTransientKey : CommandKind.UnregisterTransientKey, "Ctrl+Z");
        }
    }

    private void OnHotZoneElapsed(HotZoneElapsed e)
    {
        if (e.Token != _hotZoneToken) return;
        if (!_settings.RevealOnPointer || _line.IsEmpty || _blocked) return;
        Reveal(false);
    }

    private void OnTopBandClicked(TopBandClicked e)
    {
        _menuBarSuppressed = true;
        _pointerInHotZone = false;
        _hotZoneSinceMs = 0;
        _hotZoneToken = 0;
        Hide();
    }

    private void OnToggleLine()
    {
        if (_isRevealed)
        {
            Hide();
            return;
        }
        _pinned = true;
        _keepOpen = true;
        _openedOnPurpose = true;
        Reveal(true);
    }

    private void OnNewItem(NewItem e)
    {
        if (string.IsNullOrWhiteSpace(e.Path)) return;
        if (_line.ContainsLivePath(e.Path)) return; // F-09

        var item = new Pegged
        {
            Path = e.Path,
            Origin = e.Origin,
            IsRecording = e.IsRecording,
            ByHand = e.ByHand,
            Kept = e.ByHand,
            InOwnDirectory = e.InOwnDirectory,
            Tilt = e.Tilt,
            CreatedAtMs = _nowMs,
        };
        var outcome = _line.Hang(item);
        if (outcome.Duplicate) return;

        if (outcome.Evicted is not null) Emit(CommandKind.StartFall, outcome.Evicted.Path, Layout.FallMs);

        // V-29：系统截图自带快门声，不补提示音；复制图片 / 监视文件夹来的才响。
        if (e.Origin != ItemOrigin.Screenshot && _settings.SoundOn)
            Emit(CommandKind.PlaySound, SoundCue.Tink.ToString(), 0.35);

        // V-26 / V-27 / V-30：最多 2 个并发飞行；减少动效时直接落位。
        if (!_env.ReduceMotion && _activeFlights < Layout.MaxConcurrentFlights)
        {
            _activeFlights++;
            Emit(CommandKind.StartFlight, e.Path, Layout.FlightMs);
            Schedule(Layout.FlightMs, new FlightFinished());
        }
        else
        {
            Emit(CommandKind.ReloadThumbnail, e.Path);
        }

        UpdateTray();
        Persist();

        if (_settings.PeekOnNewContent)
        {
            Reveal(false);
            SetPeek(Layout.PeekMs);
        }
        if (_line.IsEmpty) ScheduleEmptyHide();
    }

    private void OnTakeDown(TakenDownBatch batch)
    {
        if (batch.Entries.Count == 0) return;
        for (int i = 0; i < batch.Entries.Count; i++)
        {
            Schedule(i * Layout.BatchStepMs, new BatchStepElapsed(i, batch.Entries[i].Path));
        }
        if (_settings.SoundOn) Emit(CommandKind.PlaySound, SoundCue.Pop.ToString(), 0.25); // 只有第一声
        _lastDiscardMs = _nowMs + (batch.Entries.Count - 1) * Layout.BatchStepMs;
        _gapToken = ++_seq;
        Schedule(_lastDiscardMs - _nowMs + Layout.GapCloseMs, new GapCloseElapsed(_gapToken));
        UpdateTray();
        Persist();
        if (_line.IsEmpty) ScheduleEmptyHide();
    }

    private void OnPutBack()
    {
        var batch = _line.Undo.Pop();
        if (batch is null || batch.Entries.Count == 0)
        {
            Emit(CommandKind.Beep);
            return;
        }
        foreach (var entry in batch.Entries.Where(x => x.TrashedPath is not null))
            Emit(CommandKind.MoveFile, entry.TrashedPath + "->" + entry.Path);

        var restored = _line.PutBack(batch);
        if (restored.Count == 0) Emit(CommandKind.Beep);
        var protect = restored.Select(x => x.Id).ToHashSet();
        _line.TrimToCapacity(protect); // D-06：满绳淘汰旧照片，但不淘汰刚恢复的

        if (_settings.SoundOn) Emit(CommandKind.PlaySound, SoundCue.Tink.ToString(), 0.35);
        Reveal(false);
        SetPeek(Layout.PeekMs);
        UpdateTray();
        Persist();
    }

    private void OnHangEverythingBack()
    {
        var held = _line.Items.Where(i => i.Held).ToArray();
        foreach (var item in held)
        {
            _line.SetHeld(item.Id, false);
            Emit(CommandKind.CloseHeldWindow, item.Id.ToString());
        }
        if (held.Length == 0) return;
        Reveal(false);
        SetPeek(Layout.PeekMs);
        UpdateTray();
        Persist();
    }

    private void OnFileDisappeared(FileDisappeared e)
    {
        if (!_line.RemoveByPath(e.Path)) return;
        Emit(CommandKind.StartFall, e.Path, Layout.FallMs);
        UpdateTray();
        Persist();
        if (_line.IsEmpty) ScheduleEmptyHide();
    }

    private void OnFullScreenChanged(FullScreenChanged e)
    {
        _blocked = e.Active && !_settings.ShowOverFullScreen;
        if (_blocked) Hide();
        Schedule(Layout.FullScreenRecheckMs, new FullScreenRecheckElapsed());
    }

    private void OnFullScreenRecheck()
    {
        _blocked = _env.IsFullScreenAppActive && !_settings.ShowOverFullScreen;
        if (_blocked) Hide();
    }

    private void OnSetShowOverFullScreen(SetShowOverFullScreen e)
    {
        _settings.ShowOverFullScreen = e.On;
        _blocked = _env.IsFullScreenAppActive && !e.On;
        if (_blocked) Hide();
        Persist();
    }

    private void OnSetClipboardEnabled(SetClipboardEnabled e)
    {
        _settings.ClipboardEnabled = e.Enabled;
        Emit(e.Enabled ? CommandKind.RegisterClipboardListener : CommandKind.UnregisterClipboardListener);
        UpdateTray();
        Persist();
    }

    private void OnSetKeep(SetKeepOnLine e)
    {
        if (!Settings.IsValidKeep(e.Value))
        {
            Emit(CommandKind.Beep);
            return;
        }
        _settings.KeepOnLine = e.Value;
        _line.SetKeep(e.Value);
        Persist();
    }

    private void OnSetSize(SetSize e)
    {
        _settings.LineSize = e.Size;
        _line.Configure(_env.ScreenWidthDip, e.Size, _settings.KeepOnLine);
        Emit(CommandKind.ReloadThumbnail, "all");
        Persist();
    }

    private void OnSetInboxEnabled(SetInboxEnabled e)
    {
        _settings.InboxEnabled = e.Enabled;
        Emit(e.Enabled ? CommandKind.WatchFolder : CommandKind.UnwatchFolder, _env.ScreenshotsFolder);
        Persist();
    }

    private void OnSetShortcut(SetShortcut e)
    {
        Emit(CommandKind.UnregisterHotKey, _settings.Shortcut);
        _settings.Shortcut = e.Shortcut;
        Emit(CommandKind.RegisterHotKey, e.Shortcut);
        Persist();
    }

    private void OnSetLaunchAtLogin(SetLaunchAtLogin e)
    {
        _settings.LaunchAtLogin = e.Enabled;
        Emit(CommandKind.SetLaunchAtLogin, e.Enabled ? "true" : "false");
        Persist();
    }

    private void OnAddWatchedFolder(AddWatchedFolder e)
    {
        if (string.IsNullOrWhiteSpace(e.Path) ||
            _settings.ExtraFolders.Count >= Settings.MaxExtraFolders ||
            _settings.ExtraFolders.Any(f => string.Equals(f, e.Path, StringComparison.OrdinalIgnoreCase)))
        {
            Emit(CommandKind.Beep);
            return;
        }
        _settings.ExtraFolders.Add(e.Path);
        Emit(CommandKind.WatchFolder, e.Path);
        Persist();
    }

    private void OnRemoveWatchedFolder(RemoveWatchedFolder e)
    {
        _settings.ExtraFolders.RemoveAll(f => string.Equals(f, e.Path, StringComparison.OrdinalIgnoreCase));
        Emit(CommandKind.UnwatchFolder, e.Path);
        Persist();
    }

    private void OnDestroyElapsed(DestroyElapsed e)
    {
        if (e.Token != _destroyToken || _isRevealed) return;
        Emit(CommandKind.DestroyWindow);
        _isPresent = false;
        if (_borrowedCtrlZ)
        {
            Emit(CommandKind.UnregisterTransientKey, "Ctrl+Z");
            _borrowedCtrlZ = false;
        }
    }

    private void OnGapCloseElapsed(GapCloseElapsed e)
    {
        if (e.Token != _gapToken) return;
        if (_nowMs - _lastDiscardMs < Layout.GapCloseMs || _pointerInLineArea)
        {
            Schedule(100, new GapCloseElapsed(e.Token));
            return;
        }
        _line.CloseGaps();
        Persist();
    }

    private void OnWelcomeElapsed()
    {
        _welcomePinned = false;
        if (CanHide()) Hide();
    }

    private void OnInboxOfferElapsed()
    {
        if (_settings.InboxOffered) return;
        _settings.InboxOffered = true;
        Emit(CommandKind.ShowInboxOffer);
        Persist();
    }
}
