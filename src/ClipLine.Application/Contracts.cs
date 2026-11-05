using ClipLine.Domain;

namespace ClipLine.Application;

public enum TrayState
{
    /// <summary>默认轮廓（E-tray-16.svg）。</summary>
    Default,

    /// <summary>开启「挂上复制的图片」时的填充态（E-tray-16-active.svg）。</summary>
    Monitoring,

    /// <summary>绳上为空时变暗（E-tray-16-idle.svg）。</summary>
    Dimmed,
}

public enum SoundCue
{
    Tink,
    Pop,
    Trash,
    Beep,
}

/// <summary>
/// 门面唯一允许产生的外部副作用。平台层按 Kind 执行；测试里用 RecordingCommandSink 全部 fake。
/// 离散事件进 → 状态快照 + 命令出，这是 v1 唯一测试接缝（Issue #1 / #3 定稿）。
/// </summary>
public enum CommandKind
{
    PresentWindow,
    DestroyWindow,
    SetHitTest,
    StartFlight,
    StartFall,
    StartBreeze,
    ReloadThumbnail,
    PlaySound,
    Beep,
    SetTrayIcon,
    SetTrayTooltip,
    PersistState,
    RestoreSettings,
    WriteClipboardImage,
    WriteClipboardFile,
    UpdateClipboardFileReference,
    MoveToRecycleBin,
    MoveFile,
    SaveCopiedPng,
    WatchFolder,
    UnwatchFolder,
    RegisterClipboardListener,
    UnregisterClipboardListener,
    StartIntegrityRetry,
    StopIntegrityRetry,
    RegisterHotKey,
    UnregisterHotKey,
    RegisterTransientKey,
    UnregisterTransientKey,
    OpenFolder,
    OpenBrowser,
    OpenDefaultApp,
    ShowInExplorer,
    ShowTrayMenu,
    HideTrayMenu,
    ShowContextMenu,
    ShowQuickLook,
    CloseQuickLook,
    ShowMarkupEditor,
    ShowTrimEditor,
    ShowHeldWindow,
    CloseHeldWindow,
    ShowWelcome,
    ShowInboxOffer,
    SetLaunchAtLogin,
    SetReduceMotion,
    SetTheme,
    SetFlightOrigin,
    CopyFile,
    ShowCopiedBadge,
    HideCopiedBadge,
    SetDragPreview,
    RollbackShortcut,
    RecheckDrop,
    RewriteInOriginalFormat,
    SetTrimRange,
    ExportTrimPassthrough,
    ReplaceFileAtomically,
    SetHeldWindowScale,
    SetHeldWindowActualSize,
    UpdateQuickLookIndex,
    SetLanguageText,
    ApplyLocalizedText,
    ShowShortcutRecorder,
    ShowFolderPicker,
    // ---- 自动更新（v1.5 / 方案 D，双通道）----
    RunUpdateCheck,
    DownloadUpdate,
    InstallUpdate,
    ShowUpdateNotice,
}

/// <summary>自动更新的外部可观察阶段；Idle/Offline 不打扰用户。</summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    Available,
    Downloading,
    Verifying,
    Ready,
    Installing,
    UpToDate,
    Failed,
    Offline,
}

/// <summary>更新状态快照的一部分，唯一接缝的「出」。</summary>
public sealed record UpdateSnapshot
{
    public UpdatePhase Phase { get; init; } = UpdatePhase.Idle;
    public string CurrentVersion { get; init; } = string.Empty;
    public string AvailableVersion { get; init; } = string.Empty;
    public string Channel { get; init; } = "portable";
    public string LocalPackage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int Progress { get; init; }
    public bool Manual { get; init; }
}

public sealed record AppCommand(CommandKind Kind, string? Argument = null, double Number = 0);

public sealed record ItemSnapshot(
    Guid Id,
    string Path,
    bool Kept,
    bool Held,
    bool ByHand,
    bool Falling,
    bool IsRecording,
    double Tilt,
    CornerAction Corner,
    bool CopiedBadge);

/// <summary>外部可观察状态的完整快照；断言只允许读这里，不允许窥探私有字段。</summary>
public sealed record StateSnapshot
{
    public long NowMs { get; init; }
    public bool IsPresent { get; init; }
    public bool IsRevealed { get; init; }
    public bool Wanted { get; init; }
    public bool Blocked { get; init; }
    public bool Pinned { get; init; }
    public bool PeekActive { get; init; }
    public bool MenuBarSuppressed { get; init; }
    public bool OpenedOnPurpose { get; init; }
    public bool KeepOpen { get; init; }
    public bool IsShowingMenu { get; init; }
    public bool IsDragging { get; init; }
    public bool CardPressed { get; init; }
    public int LiveCount { get; init; }
    public int VisibleCount { get; init; }
    public int MaxItems { get; init; }
    public int Scroll { get; init; }
    public int UndoCount { get; init; }
    public int KeepOnLine { get; init; }
    public CardSize Size { get; init; }
    public bool ClipboardEnabled { get; init; }
    public bool SoundOn { get; init; }
    public bool InboxEnabled { get; init; }
    public bool RevealOnPointer { get; init; }
    public bool PeekOnNewContent { get; init; }
    public bool ShowOverFullScreen { get; init; }
    public bool LaunchAtLogin { get; init; }
    public string Shortcut { get; init; } = "Ctrl+Alt+T";
    public string Language { get; init; } = "zh-Hans";
    public IReadOnlyList<string> ExtraFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ItemSnapshot> Items { get; init; } = Array.Empty<ItemSnapshot>();
    public TrayState Tray { get; init; }
    public bool ReduceMotion { get; init; }
    public bool DarkTheme { get; init; }
    public bool HighContrast { get; init; }
    public int ActiveFlights { get; init; }
    public bool WelcomePinned { get; init; }
    public UpdateSnapshot Update { get; init; } = new();
}

public sealed record AppResult(StateSnapshot State, IReadOnlyList<AppCommand> Commands);

/// <summary>所有输入都是离散事件；这是唯一接缝的「进」。</summary>
public abstract record AppInput;

public sealed record ApplicationStarted(string PersistedJson, bool FreshInstall) : AppInput;
public sealed record ApplicationExiting : AppInput;
public sealed record PointerMoved(int X, int Y) : AppInput;
public sealed record TopBandClicked(int X, int Y) : AppInput;
public sealed record ToggleLine : AppInput;
public sealed record MenuToggled(bool Open) : AppInput;
public sealed record DragStateChanged(bool Dragging) : AppInput;
public sealed record CardPressedChanged(bool Pressed) : AppInput;
public sealed record NewItem(
    string Path,
    ItemOrigin Origin = ItemOrigin.Screenshot,
    bool IsRecording = false,
    bool ByHand = false,
    bool InOwnDirectory = true,
    double Tilt = 0) : AppInput;
public sealed record TakeDownAll : AppInput;
public sealed record TakeDownIds(IReadOnlyList<Guid> Ids) : AppInput;
public sealed record PutBack : AppInput;
public sealed record HangEverythingBack : AppInput;
public sealed record Scroll(double Delta, bool Precise) : AppInput;
public sealed record FileDisappeared(string Path) : AppInput;
public sealed record FullScreenChanged(bool Active) : AppInput;
public sealed record SetClipboardEnabled(bool Enabled) : AppInput;
public sealed record SetKeepOnLine(int Value) : AppInput;
public sealed record SetSize(CardSize Size) : AppInput;
public sealed record SetSound(bool On) : AppInput;
public sealed record SetRevealOnPointer(bool On) : AppInput;
public sealed record SetPeekOnNewContent(bool On) : AppInput;
public sealed record SetShowOverFullScreen(bool On) : AppInput;
public sealed record SetInboxEnabled(bool Enabled) : AppInput;
public sealed record SetShortcut(string Shortcut) : AppInput;
public sealed record SetLaunchAtLogin(bool Enabled) : AppInput;
public sealed record AddWatchedFolder(string Path) : AppInput;
public sealed record RemoveWatchedFolder(string Path) : AppInput;
public sealed record OpenScreenshotsFolder : AppInput;
public sealed record CheckForUpdates : AppInput;

// ---- 自动更新：离散事件进、命令出，网络与安装全部留在平台层 ----
/// <summary>启动后一次性自动检查（非轮询）；由组合根在延迟后送入。</summary>
public sealed record AutoUpdateTick : AppInput;
/// <summary>更新服务回灌的检查结果；Error 非空表示离线或失败。</summary>
public sealed record UpdateCheckCompleted(
    string? Version, string? AssetUrl, string? Sha256Url, string? Error) : AppInput;
/// <summary>下载阶段的进度，仅用于状态快照，不触发命令。</summary>
public sealed record UpdateProgress(int Percent) : AppInput;
/// <summary>下载 + SHA-256 校验完成；LocalPackage 是可安装的本地文件。</summary>
public sealed record UpdateDownloadCompleted(string LocalPackage, string? Error) : AppInput;
/// <summary>安装执行结果；成功时便携通道已由 helper 重启应用。</summary>
public sealed record UpdateInstallCompleted(bool Success, string? Error) : AppInput;

// ---- 内部调度事件（假时钟推进时触发，外部不构造） ----
internal sealed record HotZoneElapsed(long Token) : AppInput;
internal sealed record RetractElapsed(long Token) : AppInput;
internal sealed record PeekExpired(long Token) : AppInput;
internal sealed record EmptyHideElapsed(long Token) : AppInput;
internal sealed record DestroyElapsed(long Token) : AppInput;
internal sealed record GapCloseElapsed(long Token) : AppInput;
internal sealed record FullScreenRecheckElapsed : AppInput;
internal sealed record WelcomeElapsed : AppInput;
internal sealed record InboxOfferElapsed : AppInput;
internal sealed record BatchStepElapsed(int Index, string Path) : AppInput;
internal sealed record CopiedBadgeElapsed(Guid ItemId) : AppInput;
internal sealed record FlightFinished : AppInput;
internal sealed record IntegrityRetryElapsed(string Path) : AppInput;
internal sealed record DragRecheckElapsed(string Path) : AppInput;

/// <summary>环境只读查询；副作用一律走 AppCommand。</summary>
public interface IEnvironment
{
    int ScreenWidthDip { get; }
    double DisplayScale { get; }
    bool IsFullScreenAppActive { get; }
    bool ReduceMotion { get; }
    bool IsDarkTheme { get; }
    bool IsHighContrast { get; }
    bool FileExists(string path);
    bool CanDecode(string path);
    string OwnDirectory { get; }
    string ScreenshotsFolder { get; }
    string Version { get; }
    /// <summary>是否以 MSIX 包形态运行；决定自动更新走 PackageManager 还是便携 helper。默认实现避免破坏既有 fake。</summary>
    bool IsPackaged => false;
    double SystemDoubleClickIntervalMs { get; }
}

public interface IClock
{
    long NowMs { get; }
}

/// <summary>命令接收器：产品里是平台层，测试里是 RecordingCommandSink（剪贴板/目录/拖放/合成器的 fake）。</summary>
public interface ICommandSink
{
    void Execute(AppCommand command);
}

public sealed class AppDependencies
{
    public required IClock Clock { get; init; }
    public required IEnvironment Environment { get; init; }
    public ICommandSink? Sink { get; init; }
    public string? PersistedJson { get; init; }
}
