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
        Directory.CreateDirectory(_env.OwnDirectory);
        Directory.CreateDirectory(_env.ScreenshotsFolder);

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
    public double Scale => _scale;
    /// <summary>状态文件路径；冒烟注入隔离路径，避免读写真实用户设置。</summary>
    public string StatePath { get; }
    public bool TrayAdded { get; private set; }
    public IReadOnlyList<string> SinkErrors => _sink.Errors;
    internal ShellEnvironment Environment => _env;
    internal RealInputBridge? Bridge => _bridge;

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
        if (!Rope.IsVisible) return;
        RopeFrame frame = CurrentView();
        if (_hasRendered && frame.Signature == _lastSignature) return;
        _lastSignature = frame.Signature;
        _hasRendered = true;
        Rope.Present(frame);
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
        RopeFrame frame = CurrentView();
        Rope.Present(frame);
        _lastSignature = frame.Signature;
        _hasRendered = true;
    }

    public void UpdateHitTest(int pointerXpx, int pointerYpx)
    {
        if (!Rope.IsVisible) return;
        Rope.SetPassthrough(!(_cardsHitEnabled && Rope.HitTest(pointerXpx, pointerYpx)));
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
        foreach (ItemSnapshot item in s.Items)
        {
            cards.Add(new CardVisual(item.Id, item.Path, item.Kept));
            signature.Append('|').Append(item.Id).Append(':').Append(item.Path)
                .Append(':').Append(item.Kept ? 1 : 0).Append(item.Held ? 1 : 0);
        }
        return new RopeFrame(s.Size, s.VisibleCount, s.Items.Count, kept, cards, signature.ToString());
    }

    internal void OnShellCommand(AppCommand command)
    {
        switch (command.Kind)
        {
            case CommandKind.PresentWindow:
                _cardsHitEnabled = true;
                _lastSignature = CurrentView().Signature;
                _hasRendered = true;
                Rope.Present(CurrentView());
                break;
            case CommandKind.SetHitTest:
                _cardsHitEnabled = command.Argument == "cards";
                if (!_cardsHitEnabled)
                {
                    Rope.SetPassthrough(true);
                    Rope.Hide();
                }
                break;
            case CommandKind.DestroyWindow:
                _cardsHitEnabled = false;
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
        Tray.Dispose();
        _sink.Dispose();
    }
}

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
        CommandKind.PlaySound,
        CommandKind.Beep,
        CommandKind.ShowFolderPicker,
        CommandKind.ShowShortcutRecorder,
        CommandKind.ReloadThumbnail,
        CommandKind.WatchFolder,
        CommandKind.UnwatchFolder,
        CommandKind.RegisterClipboardListener,
        CommandKind.UnregisterClipboardListener,
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
            case CommandKind.PlaySound:
            case CommandKind.Beep:
            case CommandKind.ShowFolderPicker:
            case CommandKind.ShowShortcutRecorder:
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
