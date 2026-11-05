using System.Globalization;
using System.Text;
using ClipLine.Application;
using ClipLine.Domain;
using ClipLine.Platform;

namespace ClipLine.Shell;

/// <summary>
/// 组合根：真实/假时钟 + 真实环境 + 组合命令接收器 + 门面。
/// 渲染与托盘命令由壳消费，文件/注册表/监听命令转给 PlatformCommandSink。
/// 交互模式由 30 fps 帧驱动 <see cref="Tick"/>；冒烟模式用 FakeClock 确定性推进。
/// v1 起 <see cref="StartRealTime"/> 挂上真实剪贴板、目录、托盘菜单与卡片输入。
/// </summary>
public sealed class ShellAppHost : IDisposable
{
    private readonly IClock _clock;
    private readonly ShellEnvironment _env;
    private readonly ShellCommandSink _sink;
    private readonly double _scale;
    private bool _cardsHitEnabled;
    private bool _disposed;
    private string _lastSignature = string.Empty;
    private bool _hasRendered;
    private RealInputBridge? _bridge;
    private readonly RopeMotion _motion;
    private readonly HashSet<Guid> _knownIds = new();
    private readonly FallWindow _fallWindow;
    private readonly ToolHost _tools;
    private readonly System.Threading.SynchronizationContext? _sync;
    private UpdateService? _updates;
    private UpdateWindow? _updateWindow;
    private UpdatePhase _lastUpdatePhase = UpdatePhase.Idle;
    private bool _updateDismissed;
    private bool _restartRequested;
    private int _hoverCard = -1;
    private readonly List<FallEntry> _falls = new();
    // 卡片下线前的最后一次屏幕布局，供错峰 60 ms 的批量 StartFall 找到各自起点。
    private readonly Dictionary<string, (int X, int Y, int W, int H, double Tilt, bool Kept, long AtMs)> _cardLayouts = new();

    public ShellAppHost(IClock? clock = null, ShellEnvironment? env = null, string? statePath = null)
    {
        // 必须作为构造参数：命令接收器在构造函数内创建，
        // 若用 init 属性则在对象初始化器阶段才赋值，隔离路径来不及生效。
        StatePath = statePath ?? PlatformCommandSink.StatePath;
        // 清单已声明 PerMonitorV2 时此调用返回 false，属正常，不视为失败。
        ShellNative.SetProcessDpiAwarenessContext(new IntPtr(-4));
        _scale = ShellNative.GetDpiForSystem() / 96.0;
        if (_scale <= 0) _scale = 1.0;

        _clock = clock ?? new SystemClock();
        _env = env ?? ShellEnvironment.CreateDefault();
        // 更新服务在线程池完成网络/下载，结果必须 Post 回 UI 线程才算「同一个门面」。
        _sync = System.Threading.SynchronizationContext.Current;
        // 注入时钟 = 冒烟/单测，阵风随机源固定成可复现序列；真实运行用时间种子。
        _motion = new RopeMotion(clock is null ? 0 : 20261105);
        _fallWindow = new FallWindow(_scale);
        _tools = new ToolHost(this);
        Directory.CreateDirectory(_env.OwnDirectory);
        Directory.CreateDirectory(_env.ScreenshotsFolder);
        // 系统绘制部分（右键菜单等）也要跟随深浅色，启动时先对齐一次。
        ThemeHook.Apply();

        Rope = new RopeWindow(_scale);
        Tray = new ShellTray();
        _sink = new ShellCommandSink(this);
        Facade = new AppFacade(new AppDependencies
        {
            Clock = _clock,
            Environment = _env,
            Sink = _sink,
        });
    }

    public AppFacade Facade { get; }
    public RopeWindow Rope { get; }
    public ShellTray Tray { get; }
    /// <summary>四个系统替身窗口；真实点击验证与冒烟都从这里观察外部可见行为。</summary>
    internal ToolHost Tools => _tools;

    /// <summary>自动更新服务：结果以离散事件 Post 回唯一门面，不新增第二个接缝。</summary>
    internal UpdateService Updates => _updates ??= new UpdateService(DispatchInput, _env.IsPackaged);

    private void DispatchInput(AppInput input)
    {
        if (_sync is not null) _sync.Post(_ => Facade.Handle(input), null);
        else Facade.Handle(input);
    }

    /// <summary>更新已交给一次性 helper，主进程需退出以便替换文件 / 注册新包。</summary>
    public event Action? RestartRequested;
    public double Scale => _scale;
    /// <summary>状态文件路径；冒烟注入隔离路径，避免读写真实用户设置。</summary>
    public string StatePath { get; }
    public bool TrayAdded { get; private set; }
    public IReadOnlyList<string> SinkErrors => _sink.Errors;
    internal ShellEnvironment Environment => _env;
    internal RealInputBridge? Bridge => _bridge;
    /// <summary>掉落层当前是否可见（冒烟/验证用的可观察行为，不是新接缝）。</summary>
    internal bool FallWindowVisible => _fallWindow.IsVisible;
    internal bool DumpFallFrame(string path) => _fallWindow.DumpFrame(path);

    /// <summary>菜单审计：切换无副作用路由，并把「存储到桌面」重定向到隔离目录。</summary>
    internal void ConfigureAudit(bool dryRun, string desktopDirectory)
    {
        _sink.DryRun = dryRun;
        _sink.SetDesktopDirectory(desktopDirectory);
    }

    internal void ResetAudit() => _sink.ResetAudit();
    internal IReadOnlyList<string> AuditUnhandled => _sink.RouteUnhandled;
    internal IReadOnlyList<string> AuditPlatformErrors => _sink.PlatformErrors;
    internal IReadOnlyList<string> AuditPlatformUnhandled => _sink.PlatformUnhandled;

    /// <summary>菜单「退出 ClipLine」/ 会话结束触发；v0 的「右键即退出」已废弃。</summary>
    public event Action? QuitRequested;

    public event Action? ExitRequested
    {
        add => QuitRequested += value;
        remove => QuitRequested -= value;
    }

    public void Start()
    {
        TrayAdded = Tray.Add();
        string path = StatePath;
        string json = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        Facade.Handle(new ApplicationStarted(json, json.Length == 0));
    }

    /// <summary>
    /// 交互模式专属：挂上 Win32 消息驱动的真实输入（零轮询）。
    /// 冒烟模式不调用，保证 128 项确定性用例不受真实剪贴板/目录影响。
    /// </summary>
    public void StartRealTime()
    {
        if (_bridge is not null) return;
        _bridge = new RealInputBridge(this);
        _bridge.Attach();
    }

    /// <summary>
    /// 输入桥在离散事件后推进门面时钟。统一走注入的时钟，
    /// 避免桥里硬编码 Environment.TickCount64 让假时钟测试失效。
    /// </summary>
    internal void AdvanceFrame() => Facade.AdvanceTo(_clock.NowMs);

    /// <summary>一帧：物理像素指针进门面（换算 DIP），推进时钟，再做卡片级命中切换。</summary>
    public void Tick(int pointerXpx, int pointerYpx)
    {
        int xDip = (int)Math.Round(pointerXpx / _scale);
        int yDip = (int)Math.Round(pointerYpx / _scale);
        Facade.Handle(new PointerMoved(xDip, yDip));
        Facade.AdvanceTo(_clock.NowMs);
        RenderFrame();
        UpdateHitTest(pointerXpx, pointerYpx);
    }

    /// <summary>
    /// 帧驱动重绘：门面的 PresentWindow 只在窗口首次呈现时发一次，
    /// 之后新图、固定、取下等状态变化由渲染壳按帧拉取快照并按内容指纹重绘。
    /// </summary>
    public void RenderFrame()
    {
        AdvanceMotion();
        RenderFalls();
        StateSnapshot state = Facade.Current;
        SyncUpdatePanel(state);
        // 安装器已由一次性 helper 接管：主进程必须退出，helper 才能替换文件 / 注册新包并重启。
        if (!_restartRequested && state.Update.Phase == UpdatePhase.Installing)
        {
            _restartRequested = true;
            RestartRequested?.Invoke();
            return;
        }
        if (!Rope.IsVisible && state.IsPresent && state.IsRevealed)
        {
            // 收起后 DestroyDelayMs(0.3 s) 内又被唤出（热区 / 「放回绳上」/ 新图）
            // 时，门面仍认为窗口存在，不会重发 PresentWindow；SetHitTest=cards
            // 只改命中不改可见性，而 DestroyElapsed 又因 _isRevealed=true 跳过销毁，
            // 分层窗口会永久停在隐藏态 —— 表现为「鼠标到顶部也唤不出绳子」。
            // 渲染壳按状态帧自愈：状态说要展开，窗口就必须可见。
            RopeFrame restored = CurrentView();
            _lastSignature = restored.Signature;
            _hasRendered = true;
            Rope.Present(restored);
            RememberLayouts(restored);
            return;
        }
        if (!Rope.IsVisible) return;
        RopeFrame frame = CurrentView();
        if (_hasRendered && frame.Signature == _lastSignature) return;
        _lastSignature = frame.Signature;
        _hasRendered = true;
        Rope.Present(frame);
        RememberLayouts(frame);
    }

    /// <summary>
    /// 更新面板：手动检查立即出现（P1-7），后台下载不打扰，下载完成必须等确认（P0-3）。
    /// 自动检查的下载过程不弹窗；Ready 时才出现，因为这时需要用户决策。
    /// </summary>
    private void SyncUpdatePanel(StateSnapshot state)
    {
        UpdateSnapshot u = state.Update;
        if (u.Phase == UpdatePhase.Checking && _lastUpdatePhase != UpdatePhase.Checking)
            _updateDismissed = false; // 新的一次手动检查重新给用户反馈
        _lastUpdatePhase = u.Phase;

        bool relevant = u.Phase is UpdatePhase.Checking or UpdatePhase.Available
            or UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Ready
            or UpdatePhase.Failed or UpdatePhase.UpToDate or UpdatePhase.Offline;
        bool wantsAttention = u.Manual || u.Phase == UpdatePhase.Ready;
        bool want = relevant && wantsAttention && !u.Deferred && !_updateDismissed;

        if (!want)
        {
            _updateWindow?.Hide();
            return;
        }
        if (_updateWindow is null)
        {
            _updateWindow = new UpdateWindow(_scale, Facade);
            _updateWindow.UserDismissed += () => _updateDismissed = true;
        }
        if (!_updateWindow.IsVisible) _updateWindow.Show(u);
        else _updateWindow.Update(u);
    }

    internal UpdateWindow? UpdatePanel => _updateWindow;

    /// <summary>系统主题变化：刷新色板并让所有自绘窗口立即换色。</summary>
    public void ApplyTheme()
    {
        // 先刷系统菜单/滚动条的深浅，再判断色板是否变化：
        // 高对比度换配色方案时色板可能看上去没变，但菜单必须重刷。
        ThemeHook.Apply();
        if (!ThemePalette.Refresh()) return;
        _lastSignature = string.Empty; // 强制下一帧重绘绳
        _tools.ApplyTheme();
        _updateWindow?.ApplyTheme();
        _bridge?.ApplyTheme();
        Tray.Notice?.ApplyTheme();
        if (Rope.IsVisible)
        {
            RopeFrame frame = CurrentView();
            _lastSignature = frame.Signature;
            Rope.Present(frame);
        }
    }

    /// <summary>
    /// 掉落层每帧推进：把到期的移除，其余按 raw³ 交给 FallWindow 绘制。
    /// 没有活跃掉落时隐藏窗口，合成器与 CPU 完全空闲。
    /// </summary>
    private void RenderFalls()
    {
        long now = _clock.NowMs;
        for (int i = _falls.Count - 1; i >= 0; i--)
            if (now - _falls[i].StartMs >= _falls[i].DurationMs) _falls.RemoveAt(i);
        if (_falls.Count == 0)
        {
            if (_fallWindow.IsVisible) _fallWindow.Hide();
            return;
        }
        var cards = new List<FallCard>(_falls.Count);
        foreach (FallEntry f in _falls)
            cards.Add(new FallCard(f.Path, f.X, f.Y, f.W, f.H, f.Tilt, f.Kept,
                (now - f.StartMs) / (double)f.DurationMs));
        _fallWindow.Present(cards);
    }

    /// <summary>把当前帧的卡片几何记住，供随后到达的 StartFall 定位（批量错峰会晚于帧更新）。</summary>
    private void RememberLayouts(RopeFrame frame)
    {
        var rects = Rope.CardRects;
        int n = Math.Min(frame.Cards.Count, rects.Count);
        long now = _clock.NowMs;
        for (int i = 0; i < n; i++)
        {
            CardVisual c = frame.Cards[i];
            var r = rects[i];
            _cardLayouts[c.Path] = (r.X, r.Y, r.W, r.H, c.Tilt, c.Kept, now);
        }
        if (_cardLayouts.Count <= 48) return;
        var stale = new List<string>();
        foreach (var pair in _cardLayouts)
            if (now - pair.Value.AtMs > 3000) stale.Add(pair.Key);
        foreach (string key in stale) _cardLayouts.Remove(key);
    }

    /// <summary>消费门面 StartFall：取该卡下线前的最后位置，排队一次 0.55 s 的翻落。</summary>
    private void BeginFall(AppCommand command)
    {
        if (string.IsNullOrEmpty(command.Argument) || _env.ReduceMotion) return;
        long now = _clock.NowMs;
        long duration = command.Number > 0 ? (long)command.Number : Layout.FallMs;
        if (_cardLayouts.TryGetValue(command.Argument, out var layout))
        {
            _falls.Add(new FallEntry(command.Argument, layout.X, layout.Y, layout.W, layout.H,
                layout.Tilt, layout.Kept, now, duration));
        }
        else
        {
            // 兜底：没有布局快照时按屏幕中间的中档卡片落下，保证动作仍然可见。
            int w = (int)Math.Round(Layout.CardWidth(CardSize.Medium) * _scale);
            int h = (int)Math.Round(Layout.PhotoMaxHeight(CardSize.Medium) * _scale);
            int x = (ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN) - w) / 2;
            int y = (int)Math.Round((Layout.RopeTop + 12) * _scale);
            _falls.Add(new FallEntry(command.Argument, x, y, w, h, 0, false, now, duration));
        }
        if (_falls.Count > 24) _falls.RemoveRange(0, _falls.Count - 24);
    }

    /// <summary>
    /// 每帧同步绳上卡片与摆动模型：新卡触发 16° 摆入 + 46 DIP 落下，
    /// 到点的阵风派发 nudge，减少动效时整体静止。
    /// 内容指纹里带上摆角/落下量后，动画活跃期间签名逐帧变化，天然绕过 RenderFrame 去重；
    /// 静止时摆角恒等于静态倾角，签名稳定，不产生任何额外重绘。
    /// </summary>
    private void AdvanceMotion()
    {
        StateSnapshot s = Facade.Current;
        _motion.ReduceMotion = _env.ReduceMotion;
        var ids = new List<Guid>(s.Items.Count);
        foreach (ItemSnapshot item in s.Items)
        {
            ids.Add(item.Id);
            if (_knownIds.Add(item.Id)) _motion.OnNewItem(item.Id, _clock.NowMs);
        }
        if (_knownIds.Count != ids.Count)
        {
            var live = new HashSet<Guid>(ids);
            _knownIds.RemoveWhere(id => !live.Contains(id));
        }
        _motion.Advance(_clock.NowMs, ids, s.IsDragging);
    }

    /// <summary>
    /// 收起 0.3 s 后绳窗口会被销毁；之后第一张新图到来时门面只发状态变化，
    /// 不再重发 PresentWindow。真实输入桥用它把窗口按最新帧重建回来。
    /// 冒烟模式不调用，避免干扰「销毁后不得残留」的确定性断言。
    /// </summary>
    public void EnsurePresented()
    {
        if (!Facade.Current.IsPresent || Rope.IsVisible) return;
        _cardsHitEnabled = true;
        AdvanceMotion();
        RopeFrame frame = CurrentView();
        Rope.Present(frame);
        RememberLayouts(frame);
        _lastSignature = frame.Signature;
        _hasRendered = true;
    }

    public void UpdateHitTest(int pointerXpx, int pointerYpx)
    {
        if (!Rope.IsVisible) return;
        // 按住/拖拽期间窗口持有鼠标捕获：这时改 passthrough 会重设窗口样式，
        // 捕获随之丢失，后续 WM_MOUSEMOVE 就再也到不了绳窗口，拖放永远起不来。
        if (ShellNative.GetCapture() == Rope.Handle) return;
        int index = Rope.CardIndexAt(pointerXpx, pointerYpx);
        // P1 补充：悬停高亮与命中切换同源，避免两套判定给出不一致的结果。
        if (index != _hoverCard)
        {
            _hoverCard = index;
            Rope.SetHoverIndex(index);
        }
        Rope.SetPassthrough(!(_cardsHitEnabled && index >= 0));
    }

    internal RopeFrame CurrentView()
    {
        StateSnapshot s = Facade.Current;
        int kept = 0;
        foreach (ItemSnapshot item in s.Items)
            if (item.Kept) kept++;

        var cards = new List<CardVisual>(s.Items.Count);
        var signature = new StringBuilder();
        signature.Append((int)s.Size).Append('/').Append(s.VisibleCount).Append('/')
            .Append(s.Items.Count).Append('/').Append(kept).Append('/')
            .Append(s.IsRevealed ? 1 : 0).Append('/').Append(s.Scroll);
        long nowMs = _clock.NowMs;
        foreach (ItemSnapshot item in s.Items)
        {
            // 静态倾角 ±2.5° 来自 Pegged.Tilt，动态摆角来自 RopeMotion；
            // 两者相加才是 Swift 的 rotationEffect(.degrees(swing + item.tilt))。
            double swing = _motion.AngleFor(item.Id, nowMs) + item.Tilt;
            double drop = _motion.DropDipFor(item.Id, nowMs);
            cards.Add(new CardVisual(item.Id, item.Path, item.Kept, item.CopiedBadge,
                swing, drop, item.Tilt));
            signature.Append('|').Append(item.Id).Append(':').Append(item.Path)
                .Append(':').Append(item.Kept ? 1 : 0).Append(item.Held ? 1 : 0)
                .Append(item.CopiedBadge ? 'C' : '-')
                .Append(':').Append(swing.ToString("F2", CultureInfo.InvariantCulture))
                .Append(':').Append(drop.ToString("F1", CultureInfo.InvariantCulture));
        }
        return new RopeFrame(s.Size, s.VisibleCount, s.Items.Count, kept, cards, signature.ToString());
    }

    internal void OnShellCommand(AppCommand command)
    {
        switch (command.Kind)
        {
            case CommandKind.PresentWindow:
                _cardsHitEnabled = true;
                AdvanceMotion();
                RopeFrame presented = CurrentView();
                _lastSignature = presented.Signature;
                _hasRendered = true;
                Rope.Present(presented);
                RememberLayouts(presented);
                break;
            case CommandKind.SetHitTest:
                _cardsHitEnabled = command.Argument == "cards";
                if (!_cardsHitEnabled)
                {
                    Rope.SetPassthrough(true);
                    Rope.Hide();
                    _hoverCard = -1;
                }
                break;
            case CommandKind.DestroyWindow:
                _cardsHitEnabled = false;
                _hoverCard = -1;
                Rope.DestroyNow();
                break;
            case CommandKind.SetTrayIcon:
                if (Enum.TryParse<TrayState>(command.Argument, out TrayState state)) Tray.SetState(state);
                break;
            case CommandKind.SetTrayTooltip:
                Tray.SetTooltip((command.Argument ?? "ClipLine").Replace("|", " — "));
                break;
            case CommandKind.ReloadThumbnail:
                // 卡片尺寸变化：按新的 Size 重绘当前帧（GDI 画布不缓存缩略图）。
                RenderFrame();
                break;
            case CommandKind.ShowCopiedBadge:
                // 复制反馈只有「已复制」角标 + 3° 轻摆 + 本次重绘；
                // 不再转发输入桥弹通知窗（与角标重复，见 RealInputBridge）。
                if (Guid.TryParse(command.Argument, out Guid copiedId))
                    _motion.OnCopied(copiedId, _clock.NowMs);
                RenderFrame();
                break;
            case CommandKind.HideCopiedBadge:
                // 角标 1.2 秒后消失，不等下一帧驱动。
                _bridge?.HandleCommand(command);
                RenderFrame();
                break;
            case CommandKind.StartFall:
                BeginFall(command);
                break;
            case CommandKind.StartFlight:
                // 截图飞入仍属渲染壳遗留项（docs/08）；在这里显式吃掉，
                // 不落到平台层被误记为未处理命令，也不引入第二个测试接缝。
                break;
            case CommandKind.WatchFolder:
            case CommandKind.UnwatchFolder:
            case CommandKind.RegisterClipboardListener:
            case CommandKind.UnregisterClipboardListener:
                // 真实监听器只有输入桥这一套，不能让平台层再建第二套实例。
                _bridge?.HandleCommand(command);
                break;
            default:
                _bridge?.HandleCommand(command);
                break;
        }
    }

    internal void RequestQuit() => QuitRequested?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bridge?.Dispose();
        Rope.Dispose();
        _fallWindow.Dispose();
        _tools.Dispose();
        _updateWindow?.Dispose();
        Tray.Dispose();
        _sink.Dispose();
    }
}

/// <summary>一次排队中的掉落；坐标是绳窗口客户区物理像素。</summary>
internal sealed record FallEntry(string Path, int X, int Y, int W, int H,
    double Tilt, bool Kept, long StartMs, long DurationMs);

/// <summary>组合命令接收器：渲染/托盘/交互给壳，其余给平台层；平台异常记录而不是让门面崩掉。</summary>
internal sealed class ShellCommandSink : ICommandSink
{
    private readonly ShellAppHost _host;
    private readonly PlatformCommandSink _platform;
    private readonly List<string> _errors = new();
    private readonly List<string> _routeUnhandled = new();

    internal ShellCommandSink(ShellAppHost host)
    {
        _host = host;
        _platform = new PlatformCommandSink(host.StatePath);
    }

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> PlatformErrors => _platform.Errors;
    public IReadOnlyList<string> PlatformUnhandled => _platform.Unhandled;
    public IReadOnlyList<string> RouteUnhandled => _routeUnhandled;

    /// <summary>审计模式：只判定路由、不产生任何真实副作用（不弹菜单、不开应用、不写注册表）。</summary>
    public bool DryRun { get; set; }

    public void SetDesktopDirectory(string path) => _platform.DesktopDirectory = path;
    public void ResetAudit() => _routeUnhandled.Clear();

    private static readonly HashSet<CommandKind> ShellRouted = new()
    {
        CommandKind.PresentWindow,
        CommandKind.SetHitTest,
        CommandKind.DestroyWindow,
        CommandKind.SetTrayIcon,
        CommandKind.SetTrayTooltip,
        CommandKind.ShowWelcome,
        CommandKind.ShowInboxOffer,
        CommandKind.ShowQuickLook,
        CommandKind.CloseQuickLook,
        CommandKind.ShowContextMenu,
        CommandKind.HideTrayMenu,
        CommandKind.ShowMarkupEditor,
        CommandKind.ShowTrimEditor,
        CommandKind.ShowHeldWindow,
        CommandKind.CloseHeldWindow,
        CommandKind.WriteClipboardImage,
        CommandKind.WriteClipboardFile,
        CommandKind.ShowCopiedBadge,
        CommandKind.HideCopiedBadge,
        CommandKind.PlaySound,
        CommandKind.Beep,
        CommandKind.ShowFolderPicker,
        CommandKind.ShowShortcutRecorder,
        CommandKind.ReloadThumbnail,
        CommandKind.StartFall,
        CommandKind.StartFlight,
        CommandKind.WatchFolder,
        CommandKind.UnwatchFolder,
        CommandKind.RegisterClipboardListener,
        CommandKind.UnregisterClipboardListener,
        CommandKind.RunUpdateCheck,
        CommandKind.DownloadUpdate,
        CommandKind.InstallUpdate,
        CommandKind.CancelUpdateDownload,
        CommandKind.ShowUpdateNotice,
        CommandKind.HideUpdatePanel,
        CommandKind.RegisterHotKey,
        CommandKind.UnregisterHotKey,
        CommandKind.RollbackShortcut,
    };

    public static bool IsHandled(CommandKind kind) => ShellRouted.Contains(kind);

    public void Execute(AppCommand command)
    {
        if (DryRun)
        {
            if (!IsHandled(command.Kind) && !PlatformCommandSink.IsHandled(command.Kind))
                _routeUnhandled.Add(command.Kind.ToString());
            return;
        }

        switch (command.Kind)
        {
            case CommandKind.PresentWindow:
            case CommandKind.SetHitTest:
            case CommandKind.DestroyWindow:
            case CommandKind.SetTrayIcon:
            case CommandKind.SetTrayTooltip:
                _host.OnShellCommand(command);
                break;
            // v1 真实交互命令：由输入桥落到 Win32 菜单 / 系统默认应用 / 剪贴板 / 气泡。
            case CommandKind.ShowWelcome:
            case CommandKind.ShowInboxOffer:
            case CommandKind.ShowQuickLook:
            case CommandKind.CloseQuickLook:
            case CommandKind.ShowContextMenu:
            case CommandKind.HideTrayMenu:
            case CommandKind.ShowMarkupEditor:
            case CommandKind.ShowTrimEditor:
            case CommandKind.ShowHeldWindow:
            case CommandKind.CloseHeldWindow:
            case CommandKind.WriteClipboardImage:
            case CommandKind.WriteClipboardFile:
            case CommandKind.ShowCopiedBadge:
            case CommandKind.HideCopiedBadge:
            case CommandKind.PlaySound:
            case CommandKind.Beep:
            case CommandKind.ShowFolderPicker:
            case CommandKind.ShowShortcutRecorder:
            case CommandKind.StartFall:
            case CommandKind.StartFlight:
            case CommandKind.RunUpdateCheck:
            case CommandKind.DownloadUpdate:
            case CommandKind.InstallUpdate:
            case CommandKind.CancelUpdateDownload:
            case CommandKind.ShowUpdateNotice:
            case CommandKind.HideUpdatePanel:
            case CommandKind.RegisterHotKey:
            case CommandKind.UnregisterHotKey:
            case CommandKind.RollbackShortcut:
                _host.OnShellCommand(command);
                break;
            default:
                try
                {
                    _platform.Execute(command);
                }
                catch (Exception ex)
                {
                    _errors.Add($"{command.Kind}: {ex.GetType().Name}: {ex.Message}");
                }
                break;
        }
    }

    public void Dispose() => _platform.Dispose();
}
