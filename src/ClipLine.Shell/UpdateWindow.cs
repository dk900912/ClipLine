using System;
using System.Runtime.InteropServices;
using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>
/// 更新面板（v1.6）。取代「通知卡 3.2 秒消失 + 校验通过即安装」的黑箱：
/// 下载仍然后台自动，但安装/重启必须在这里由用户点确认；下载中可取消，随时可推迟。
/// 与工具窗一样是自绘分层窗口，支持拖动与主题切换。
/// </summary>
internal sealed class UpdateWindow : IDisposable
{
    public const string ClassName = "ClipLine_UpdateWindow";
    private const int WidthDip = 460;
    private const int HeightDip = 330;

    private static readonly Dictionary<IntPtr, UpdateWindow> Windows = new();
    private static ShellNative.WndProcDelegate? s_proc;
    private static bool s_classRegistered;

    private readonly double _scale;
    private readonly AppFacade _facade;
    private readonly UpdateLayout _layout = new();
    private IntPtr _hwnd;
    private RopeCanvas? _canvas;
    private UpdateSnapshot _state = new();
    private string _key = string.Empty;
    private int _posX, _posY;
    private bool _dragging;
    private ShellNative.POINT _dragStart;
    private ShellNative.RECT _dragRect;
    private UpdateHover _hover;
    private bool _pressed;
    private bool _disposed;

    public UpdateWindow(double scale, AppFacade facade)
    {
        _scale = scale <= 0 ? 1.0 : scale;
        _facade = facade;
    }

    public IntPtr Handle => _hwnd;
    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);
    public UpdateLayout Layout => _layout;

    /// <summary>用户主动关掉面板（非壳层自动隐藏）；宿主据此本次会话不再弹。</summary>
    public event Action? UserDismissed;

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    private static void EnsureClass()
    {
        if (s_classRegistered) return;
        s_proc = WndProc;
        var wc = new ShellNative.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.WNDCLASSEXW>(),
            Style = 0x0008,
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            HInstance = ShellNative.GetModuleHandleW(null),
            HCursor = ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcArrow),
            LpszClassName = ClassName,
        };
        if (ShellNative.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("注册更新窗口类失败，Win32Error=" +
                Marshal.GetLastWin32Error());
        s_classRegistered = true;
    }

    public bool Show(UpdateSnapshot state)
    {
        EnsureClass();
        int w = Px(WidthDip), h = Px(HeightDip);
        if (!Exists)
        {
            uint ex = ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_APPWINDOW;
            _hwnd = ShellNative.CreateWindowExW(ex, ClassName, "ClipLine — 软件更新",
                ShellNative.WS_POPUP, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero,
                ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            Windows[_hwnd] = this;
            WindowChrome.Apply(_hwnd); // P2-6 圆角 + 深色边框
            Center(w, h);
        }
        else
        {
            var rect = new ShellNative.RECT();
            ShellNative.GetWindowRect(_hwnd, out rect);
            _posX = rect.Left;
            _posY = rect.Top;
        }
        _state = state;
        _key = KeyOf(state);
        Render();
        ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOW);
        ShellNative.SetForegroundWindow(_hwnd);
        ShellNative.SetFocus(_hwnd);
        return true;
    }

    private void Center(int w, int h)
    {
        var area = new ShellNative.RECT();
        if (!ShellNative.SystemParametersInfoW(ShellNative.SPI_GETWORKAREA, 0, ref area, 0))
        {
            area.Right = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
            area.Bottom = ShellNative.GetSystemMetrics(ShellNative.SM_CYSCREEN);
        }
        int x = area.Left + (area.Right - area.Left - w) / 2;
        int y = area.Top + (area.Bottom - area.Top - h) / 2 - Px(20);
        _posX = x;
        _posY = y;
        ShellNative.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, ShellNative.SWP_NOACTIVATE);
    }

    private static string KeyOf(UpdateSnapshot s) =>
        $"{s.Phase}|{s.Progress}|{s.AvailableVersion}|{s.Message}|{s.Channel}|{s.CurrentVersion}|{s.Deferred}";

    /// <summary>同一阶段/进度只重绘一次；进度每变 1% 才更新，避免每帧重建 DIB。</summary>
    public void Update(UpdateSnapshot state)
    {
        if (!Exists || !IsVisible) return;
        string key = KeyOf(state);
        if (key == _key) return;
        _key = key;
        _state = state;
        Render();
    }

    private UpdateView View()
    {
        UpdateSnapshot s = _state;
        bool ready = s.Phase == UpdatePhase.Ready;
        bool failed = s.Phase == UpdatePhase.Failed;
        bool downloading = s.Phase is UpdatePhase.Available or UpdatePhase.Downloading;
        bool checking = s.Phase == UpdatePhase.Checking;
        bool verifying = s.Phase == UpdatePhase.Verifying;

        string message = s.Message;
        if (checking) message = "正在检查更新…";
        else if (verifying) message = "已下载，正在校验 SHA-256…";
        else if (ready) message = "已下载并通过校验。点击「立即重启更新」才会安装并重启。";
        else if (s.Phase == UpdatePhase.UpToDate) message = "当前已是最新版本，无需更新。";
        else if (s.Phase == UpdatePhase.Offline) message = "暂时无法连接更新服务：" + s.Message;
        else if (failed) message = "更新失败：" + s.Message;

        string primary = ready ? "立即重启更新"
            : failed ? "重试"
            : (checking || downloading || verifying) ? "隐藏"
            : "好";
        string secondary = ready ? "稍后"
            : downloading ? "取消下载"
            : (checking || verifying) ? "隐藏"
            : string.Empty;

        return new UpdateView(
            s.CurrentVersion.Length > 0 ? s.CurrentVersion : BuildInfo.Version,
            ready ? s.AvailableVersion : s.AvailableVersion,
            s.Channel,
            message,
            s.Progress,
            checking || verifying,
            primary,
            secondary,
            PrimaryWide: ready,
            Hover: _hover,
            Pressed: _pressed);
    }

    private void Render()
    {
        if (_canvas is null)
        {
            int w = Exists ? CurrentWidth() : Px(WidthDip);
            int h = Exists ? CurrentHeight() : Px(HeightDip);
            _canvas = new RopeCanvas(w, h, _scale) { Theme = ThemePalette.Current };
        }
        _canvas.Theme = ThemePalette.Current;
        _canvas.RenderUpdate(View(), _layout);
        _canvas.Present(_hwnd, 255, _posX, _posY);
    }

    private int CurrentWidth()
    {
        ShellNative.GetWindowRect(_hwnd, out ShellNative.RECT r);
        return Math.Max(1, r.Right - r.Left);
    }

    private int CurrentHeight()
    {
        ShellNative.GetWindowRect(_hwnd, out ShellNative.RECT r);
        return Math.Max(1, r.Bottom - r.Top);
    }

    public void ApplyTheme()
    {
        if (_canvas is null) return;
        _canvas.Theme = ThemePalette.Current;
        Render();
    }

    public bool DumpFrame(string path) => _canvas?.SavePng(path) ?? false;

    public void Hide()
    {
        if (Exists) ShellNative.ShowWindow(_hwnd, ShellNative.SW_HIDE);
        _dragging = false;
        ShellNative.ReleaseCapture();
    }

    private static bool In((int X, int Y, int W, int H) r, int x, int y) =>
        r.W > 0 && r.H > 0 && x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    private UpdateHover Hit(int x, int y)
    {
        if (In(_layout.Close, x, y)) return UpdateHover.Close;
        if (In(_layout.Primary, x, y)) return UpdateHover.Primary;
        if (In(_layout.Secondary, x, y)) return UpdateHover.Secondary;
        return UpdateHover.None;
    }

    private void Activate(UpdateHover hit, ClipLine.Application.AppFacade facade)
    {
        UpdatePhase phase = _state.Phase;
        switch (hit)
        {
            case UpdateHover.Primary:
                if (phase == UpdatePhase.Ready) facade.Handle(new UpdateInstallConfirmed());
                else if (phase == UpdatePhase.Failed) facade.Handle(new CheckForUpdates());
                else { UserDismissed?.Invoke(); Hide(); }
                break;
            case UpdateHover.Secondary:
                if (phase == UpdatePhase.Ready) facade.Handle(new UpdateDeferred());
                else if (phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Verifying)
                    facade.Handle(new CancelUpdateDownload());
                UserDismissed?.Invoke();
                Hide();
                break;
            case UpdateHover.Close:
                // 关掉「已就绪」面板等价于「稍后」，避免下次又突然弹出。
                if (phase == UpdatePhase.Ready) facade.Handle(new UpdateDeferred());
                UserDismissed?.Invoke();
                Hide();
                break;
        }
    }

    private void BeginMove()
    {
        _dragging = true;
        ShellNative.GetCursorPos(out _dragStart);
        ShellNative.GetWindowRect(_hwnd, out _dragRect);
        ShellNative.SetCapture(_hwnd);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == ShellNative.WM_SETCURSOR)
        {
            ShellNative.SetCursor(ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcArrow));
            return new IntPtr(1);
        }
        if (!Windows.TryGetValue(hWnd, out UpdateWindow? window))
            return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        (int x, int y) = ((short)(lParam.ToInt64() & 0xFFFF),
            (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        switch (msg)
        {
            case ShellNative.WM_MOUSEMOVE:
                if (window._dragging)
                {
                    ShellNative.GetCursorPos(out ShellNative.POINT p);
                    int nx = window._dragRect.Left + p.X - window._dragStart.X;
                    int ny = window._dragRect.Top + p.Y - window._dragStart.Y;
                    window._posX = nx;
                    window._posY = ny;
                    ShellNative.SetWindowPos(hWnd, IntPtr.Zero, nx, ny, 0, 0,
                        ShellNative.SWP_NOSIZE | ShellNative.SWP_NOACTIVATE);
                    return IntPtr.Zero;
                }
                UpdateHover hover = window.Hit(x, y);
                if (hover != window._hover)
                {
                    window._hover = hover;
                    var tme = new ShellNative.TRACKMOUSEEVENT
                    {
                        CbSize = (uint)Marshal.SizeOf<ShellNative.TRACKMOUSEEVENT>(),
                        DwFlags = ShellNative.TmeLeave,
                        HwndTrack = hWnd,
                    };
                    ShellNative.TrackMouseEvent(ref tme);
                    window.Render();
                }
                return IntPtr.Zero;

            case ShellNative.WM_MOUSELEAVE:
                if (!window._dragging)
                {
                    window._hover = UpdateHover.None;
                    window._pressed = false;
                    window.Render();
                }
                return IntPtr.Zero;

            case ShellNative.WM_LBUTTONDOWN:
                if (y < window.Px(44))
                {
                    window.BeginMove();
                    return IntPtr.Zero;
                }
                window._hover = window.Hit(x, y);
                window._pressed = true;
                window.Render();
                return IntPtr.Zero;

            case ShellNative.WM_LBUTTONUP:
                if (window._dragging)
                {
                    window._dragging = false;
                    ShellNative.ReleaseCapture();
                    return IntPtr.Zero;
                }
                window._pressed = false;
                UpdateHover hit = window.Hit(x, y);
                if (hit != UpdateHover.None)
                    window.Activate(hit, window._facade);
                window.Render();
                return IntPtr.Zero;

            case ShellNative.WM_KEYDOWN:
                if ((uint)wParam.ToUInt64() == ShellNative.VkEscape)
                {
                    window.UserDismissed?.Invoke();
                    window.Hide();
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;

            case ShellNative.WM_CLOSE:
                window.UserDismissed?.Invoke();
                window.Hide();
                return IntPtr.Zero;

            case ShellNative.WM_DESTROY:
                Windows.Remove(hWnd);
                return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            Windows.Remove(_hwnd);
            if (ShellNative.IsWindow(_hwnd)) ShellNative.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _canvas?.Dispose();
        _canvas = null;
    }
}
