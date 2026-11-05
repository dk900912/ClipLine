using System.Runtime.InteropServices;

namespace ClipLine.Shell;

/// <summary>
/// 轻量通知卡（v1.2）：取代 <c>Shell_NotifyIconW(NIF_INFO)</c> 的系统气泡。
/// 系统气泡的尺寸、圆角、配色与动画全部由 Windows 决定，应用无法缩小或美化；
/// 这里改用自绘分层 HWND，完整掌控 288×76 DIP（比 Win11 常见系统气泡小 20%）、
/// Fluent 风格外观与淡入/停留/淡出。
///
/// 零常驻开销：只在通知活跃期间由托盘宿主窗口的 WM_TIMER 驱动，
/// 结束后 KillTimer 并隐藏窗口，不占合成器、不轮询。
/// </summary>
internal sealed class NoticeWindow : IDisposable
{
    public const string ClassName = "ClipLine_NoticeWindow";

    /// <summary>基准：Win11 系统气泡约 360×95 DIP；×0.8 → 288×76 DIP。</summary>
    public const int WidthDip = 288;
    public const int HeightDip = 76;
    public const double SizeScale = 0.8;

    private const long FadeInMs = 180;
    private const long HoldUntilMs = 3200;
    private const long FadeOutMs = 250;
    private const long EndMs = HoldUntilMs + FadeOutMs;

    private static ShellNative.WndProcDelegate? s_proc;
    private static bool s_classRegistered;

    private readonly double _scale;
    private IntPtr _hwnd;
    private RopeCanvas? _canvas;
    private long _startMs;
    private byte _lastAlpha;
    private int _posX, _posY;
    private bool _active;
    private bool _disposed;

    public NoticeWindow(double scale) => _scale = scale <= 0 ? 1.0 : scale;

    public IntPtr Handle => _hwnd;
    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);
    public byte LastAlpha => _lastAlpha;
    public int WidthPx => _canvas?.WidthPx ?? 0;
    public int HeightPx => _canvas?.HeightPx ?? 0;
    public double Scale => _scale;

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    private static void EnsureClass()
    {
        if (s_classRegistered) return;
        s_proc = WndProc; // 保活，防 GC
        var wc = new ShellNative.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            HInstance = ShellNative.GetModuleHandleW(null),
            // 显式箭头光标，避免继承 IDC_APPSTARTING / IDC_WAIT 的加载小圆圈。
            HCursor = ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcArrow),
            LpszClassName = ClassName,
        };
        if (ShellNative.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("注册通知窗口类失败，Win32Error=" +
                Marshal.GetLastWin32Error());
        s_classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == ShellNative.WM_SETCURSOR)
        {
            ShellNative.SetCursor(ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcArrow));
            return new IntPtr(1);
        }
        return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>显示一条通知。nowMs 由调用方注入，冒烟可用假时钟确定性推进淡入淡出。</summary>
    public bool Show(string title, string text, long nowMs)
    {
        EnsureClass();
        int w = Px(WidthDip), h = Px(HeightDip);
        if (!EnsureWindow(w, h)) return false;
        Position(w, h);

        _canvas!.RenderNotice(title, text);
        _startMs = nowMs;
        _lastAlpha = 0;
        _active = true;
        ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOWNOACTIVATE);
        Tick(nowMs);
        return true;
    }

    /// <summary>按 nowMs 推进整窗透明度；返回 false 表示生命周期结束，可停止定时器。</summary>
    public bool Tick(long nowMs)
    {
        if (!_active || _canvas is null || !Exists) return false;
        long t = Math.Max(0, nowMs - _startMs);
        byte alpha;
        if (t < FadeInMs) alpha = (byte)(255 * t / FadeInMs);
        else if (t < HoldUntilMs) alpha = 255;
        else if (t < EndMs) alpha = (byte)(255 * (EndMs - t) / FadeOutMs);
        else
        {
            Hide();
            _active = false;
            return false;
        }
        if (alpha != _lastAlpha)
        {
            _lastAlpha = alpha;
            // pptDst 会重置窗口位置；必须带上 Position() 算好的右下角坐标，
            // 否则通知卡每次刷新透明度都会跳回屏幕左上角。
            _canvas.Present(_hwnd, alpha, _posX, _posY);
        }
        return true;
    }

    private bool EnsureWindow(int w, int h)
    {
        if (!Exists)
        {
            uint exStyle = ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_TRANSPARENT
                | ShellNative.WS_EX_NOACTIVATE | ShellNative.WS_EX_TOOLWINDOW;
            _hwnd = ShellNative.CreateWindowExW(exStyle, ClassName, "ClipLine notice",
                ShellNative.WS_POPUP, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero,
                ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
        }
        if (_canvas is null || _canvas.WidthPx != w || _canvas.HeightPx != h)
        {
            _canvas?.Dispose();
            _canvas = new RopeCanvas(w, h, _scale);
        }
        return true;
    }

    /// <summary>右下角贴着工作区（自动避开任务栏），不用硬编码屏幕高度。</summary>
    private void Position(int w, int h)
    {
        var area = new ShellNative.RECT();
        if (!ShellNative.SystemParametersInfoW(ShellNative.SPI_GETWORKAREA, 0, ref area, 0)
            || area.Right <= 0 || area.Bottom <= 0)
        {
            area.Left = 0;
            area.Top = 0;
            area.Right = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
            area.Bottom = ShellNative.GetSystemMetrics(ShellNative.SM_CYSCREEN);
        }
        int x = area.Right - w - Px(12);
        int y = area.Bottom - h - Px(12);
        _posX = x;
        _posY = y;
        ShellNative.SetWindowPos(_hwnd, ShellNative.HwndTopmost, x, y, w, h,
            ShellNative.SWP_NOACTIVATE);
    }

    /// <summary>导出当前通知帧，作为确定性的外观证据。</summary>
    public bool DumpFrame(string path) => _canvas?.SavePng(path) ?? false;

    public void Hide()
    {
        if (Exists) ShellNative.ShowWindow(_hwnd, ShellNative.SW_HIDE);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        Hide();
        if (_hwnd != IntPtr.Zero)
        {
            ShellNative.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _canvas?.Dispose();
        _canvas = null;
    }
}
