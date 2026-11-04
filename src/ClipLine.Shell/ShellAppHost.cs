using ClipLine.Application;
using ClipLine.Domain;
using ClipLine.Platform;

namespace ClipLine.Shell;

/// <summary>
/// 组合根：真实/假时钟 + 真实环境 + 组合命令接收器 + 门面。
/// 渲染与托盘命令由壳消费，文件/注册表/监听命令转给 PlatformCommandSink。
/// 交互模式由 30 fps 帧驱动 <see cref="Tick"/>；冒烟模式用 FakeClock 确定性推进。
/// </summary>
public sealed class ShellAppHost : IDisposable
{
    private readonly IClock _clock;
    private readonly ShellEnvironment _env;
    private readonly ShellCommandSink _sink;
    private readonly double _scale;
    private bool _cardsHitEnabled;
    private bool _disposed;
    private StateSnapshotView _lastRendered;
    private bool _hasRendered;

    public ShellAppHost(IClock? clock = null, ShellEnvironment? env = null)
    {
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
    public bool TrayAdded { get; private set; }
    public IReadOnlyList<string> SinkErrors => _sink.Errors;

    public event Action? ExitRequested
    {
        add => Tray.RightClicked += value;
        remove => Tray.RightClicked -= value;
    }

    public void Start()
    {
        TrayAdded = Tray.Add();
        string path = PlatformCommandSink.StatePath;
        string json = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        Facade.Handle(new ApplicationStarted(json, json.Length == 0));
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
    /// 之后新图、固定、取下等状态变化由渲染壳按帧拉取快照并重绘。
    /// 只在快照变化时调用 Present，避免每帧重建 DIB。
    /// </summary>
    public void RenderFrame()
    {
        if (!Rope.IsVisible) return;
        StateSnapshotView view = CurrentView();
        if (_hasRendered && view == _lastRendered) return;
        _lastRendered = view;
        _hasRendered = true;
        Rope.Present(view);
    }

    public void UpdateHitTest(int pointerXpx, int pointerYpx)
    {
        if (!Rope.IsVisible) return;
        Rope.SetPassthrough(!(_cardsHitEnabled && Rope.HitTest(pointerXpx, pointerYpx)));
    }

    internal StateSnapshotView CurrentView()
    {
        StateSnapshot s = Facade.Current;
        return new StateSnapshotView(s.Size, s.VisibleCount, s.Items.Count, CountKept(s));
    }

    private static int CountKept(StateSnapshot s)
    {
        int kept = 0;
        foreach (ItemSnapshot item in s.Items)
            if (item.Kept) kept++;
        return kept;
    }

    internal void OnShellCommand(AppCommand command)
    {
        switch (command.Kind)
        {
            case CommandKind.PresentWindow:
                _cardsHitEnabled = true;
                _lastRendered = CurrentView();
                _hasRendered = true;
                Rope.Present(_lastRendered);
                break;
            case CommandKind.SetHitTest:
                _cardsHitEnabled = command.Argument == "cards";
                if (!_cardsHitEnabled)
                {
                    // 门面语义：passthrough 只在收起时发出（AppFacade.Hide），v0 直接隐藏窗口。
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
            default:
                break; // 音效 / 动画 / 编辑器窗口属 v0 之外
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Rope.Dispose();
        Tray.Dispose();
        _sink.Dispose();
    }
}

/// <summary>组合命令接收器：渲染/托盘给壳，其余给平台层；平台异常记录而不是让门面崩掉。</summary>
internal sealed class ShellCommandSink : ICommandSink
{
    private readonly ShellAppHost _host;
    private readonly PlatformCommandSink _platform = new();
    private readonly List<string> _errors = new();

    internal ShellCommandSink(ShellAppHost host) => _host = host;

    public IReadOnlyList<string> Errors => _errors;

    public void Execute(AppCommand command)
    {
        switch (command.Kind)
        {
            case CommandKind.PresentWindow:
            case CommandKind.SetHitTest:
            case CommandKind.DestroyWindow:
            case CommandKind.SetTrayIcon:
            case CommandKind.SetTrayTooltip:
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
