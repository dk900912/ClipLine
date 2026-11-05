using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClipLine.Application;

namespace ClipLine.Shell;

/// <summary>
/// 托盘图标（E「双线」三态）：P0 结论 —— 必须挂在独立 message-only 宿主窗口，
/// 不能挂在会随收起销毁的瞬时线窗口。
/// </summary>
public sealed class ShellTray : IDisposable
{
    public const string HostClassName = "ClipLine_TrayHost";
    internal const uint CallbackMessage = ShellNative.WM_APP + 1;
    private static readonly UIntPtr NoticeTimerId = new(1);

    private static ShellNative.WndProcDelegate? s_proc;
    private static ShellTray? s_instance;

    private IntPtr _host;
    private NoticeWindow? _notice;
    private readonly Queue<(string Title, string Text)> _pendingNotices = new();
    private bool _noticeActive;
    private long _lastLeftClickMs;
    private IntPtr _iconDefault;
    private IntPtr _iconActive;
    private IntPtr _iconIdle;
    private TrayState _state = TrayState.Dimmed;
    private string _tooltip = "ClipLine";

    /// <summary>托盘右键：v1 弹出 16 项菜单（取代 v0 的「右键即退出」）。</summary>
    public event Action? MenuRequested;

    /// <summary>托盘左键：显示 / 隐藏晾衣绳。</summary>
    public event Action? LeftClicked;

    /// <summary>系统剪贴板内容变化（AddClipboardFormatListener）。</summary>
    public event Action? ClipboardUpdated;

    /// <summary>全局快捷键。参数是注册时的 id：1=晾衣绳开关，2=退出。</summary>
    public event Action<int>? HotKeyPressed;

    public event Action? SessionEnding;

    /// <summary>
    /// 系统外观变化（深色 / 浅色 / 高对比度）。托盘宿主是 message-only 窗口，
    /// 所有自绘窗口共用这一个广播源，避免每个窗口各挂一套监听。
    /// </summary>
    public event Action? ThemeChanged;

    public IntPtr HostHandle => _host;
    public bool IsAdded { get; private set; }

    public bool Add()
    {
        if (IsAdded) return true;
        RegisterHostClass();
        _host = ShellNative.CreateWindowExW(0, HostClassName, "ClipLine tray host", 0,
            0, 0, 0, 0, ShellNative.HwndMessage, IntPtr.Zero,
            ShellNative.GetModuleHandleW(null), IntPtr.Zero);
        if (_host == IntPtr.Zero) return false;
        s_instance = this;

        _iconDefault = CreateIcon(active: false, dim: false);
        _iconActive = CreateIcon(active: true, dim: false);
        _iconIdle = CreateIcon(active: false, dim: true);
        if (_iconDefault == IntPtr.Zero || _iconActive == IntPtr.Zero || _iconIdle == IntPtr.Zero) return false;

        IsAdded = Modify(ShellNative.NIM_ADD);
        return IsAdded;
    }

    public void SetState(TrayState state)
    {
        if (_state == state) return;
        _state = state;
        if (IsAdded) Modify(ShellNative.NIM_MODIFY);
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        if (IsAdded) Modify(ShellNative.NIM_MODIFY);
    }

    private bool Modify(uint message)
    {
        IntPtr icon = _state switch
        {
            TrayState.Monitoring => _iconActive,
            TrayState.Dimmed => _iconIdle,
            _ => _iconDefault,
        };
        var data = new ShellNative.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.NOTIFYICONDATAW>(),
            HWnd = _host,
            UID = 1,
            UFlags = ShellNative.NIF_ICON | ShellNative.NIF_MESSAGE | ShellNative.NIF_TIP,
            UCallbackMessage = CallbackMessage,
            HIcon = icon,
            SzTip = _tooltip,
            SzInfo = string.Empty,
            SzInfoTitle = string.Empty,
        };
        return ShellNative.Shell_NotifyIconW(message, ref data);
    }

    private void RegisterHostClass()
    {
        if (s_proc is not null) return;
        s_proc = HostWndProc; // 保活，防 GC
        var wc = new ShellNative.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            HInstance = ShellNative.GetModuleHandleW(null),
            LpszClassName = HostClassName,
        };
        ShellNative.RegisterClassExW(ref wc);
    }

    private IntPtr HostWndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        ShellTray? self = s_instance;
        if (msg == CallbackMessage)
        {
            uint mouseMsg = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouseMsg == ShellNative.WM_RBUTTONUP) self?.MenuRequested?.Invoke();
            else if (mouseMsg == ShellNative.WM_LBUTTONUP)
            {
                // 双击会先来一次 UP、再来 DBLCLK、最后又一次 UP。旧实现三路都接
                // LeftClicked，双击=三次 toggle（闪回原状）。这里只认第一次 UP，
                // 双击时间窗内的第二次 UP 直接吞掉。
                long now = Environment.TickCount64;
                if (self is not null && now - self._lastLeftClickMs > ShellNative.GetDoubleClickTime())
                    self.LeftClicked?.Invoke();
                if (self is not null) self._lastLeftClickMs = now;
            }
            else if (mouseMsg == ShellNative.WM_LBUTTONDBLCLK)
            {
                // 双击语义交给第一次单击（显示/收起），这里不再重复触发。
            }
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case ShellNative.WM_TIMER:
                if (wParam.ToUInt64() == NoticeTimerId.ToUInt64() && self is not null)
                {
                    // 通知结束后停表并出队下一条；空闲时没有任何定时器或重绘。
                    if (self._notice is null || !self._notice.Tick(Environment.TickCount64))
                    {
                        ShellNative.KillTimer(hWnd, NoticeTimerId);
                        self._noticeActive = false;
                        self.PumpNotices();
                    }
                }
                return IntPtr.Zero;
            case ShellNative.WM_CLIPBOARDUPDATE:
                self?.ClipboardUpdated?.Invoke();
                return IntPtr.Zero;
            case ShellNative.WM_HOTKEY:
                self?.HotKeyPressed?.Invoke((int)wParam.ToUInt64());
                return IntPtr.Zero;
            case ShellNative.WM_SETTINGCHANGE:
                // lParam 为 "ImmersiveColorSet" 时是深浅色切换；wParam 为
                // SPI_SETHIGHCONTRAST 时是高对比度开关；其余系统设置变化
                // 也一并重读一次色板（Refresh 未变化就不会触发重绘）。
                self?.ThemeChanged?.Invoke();
                return new IntPtr(0);
            case ShellNative.WM_THEMECHANGED:
                self?.ThemeChanged?.Invoke();
                return IntPtr.Zero;
            case ShellNative.WM_QUERYENDSESSION:
            case ShellNative.WM_ENDSESSION:
                self?.SessionEnding?.Invoke();
                return IntPtr.Zero;
            default:
                return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// 轻量通知（v1.2）。原实现用 <c>NIF_INFO</c> 系统气泡，尺寸/圆角/配色/动画
    /// 全部由 Windows 绘制，应用既不能缩小 20% 也不能美化，且通知中心关闭时会被吞掉。
    /// 现在改为自绘分层窗口；托盘宿主窗口用 WM_TIMER 驱动淡入淡出，结束即停表。
    /// 方法名保留是为了不改动门面四处命令的调用点。
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (!IsAdded) return;
        if (SuppressNotifications()) return; // 勿扰 / 演示 / 全屏游戏
        if (_noticeActive)
        {
            // 不覆盖上一条：最多排队 4 条，按顺序播放（P1-13）。
            if (_pendingNotices.Count < 4) _pendingNotices.Enqueue((title, text));
            return;
        }
        ShowNoticeNow(title, text);
    }

    private void ShowNoticeNow(string title, string text)
    {
        _notice ??= new NoticeWindow(ShellNative.GetDpiForSystem() / 96.0);
        _notice.Dismissed -= OnNoticeDismissed;
        _notice.Dismissed += OnNoticeDismissed;
        if (!_notice.Show(title, text, Environment.TickCount64)) return;
        _noticeActive = true;
        if (ShellNative.SetTimer(_host, NoticeTimerId, 33, IntPtr.Zero) == UIntPtr.Zero)
        {
            // 定时器极少数情况创建失败：至少显示一帧完整内容，不静默丢掉通知。
            _notice.Tick(Environment.TickCount64 + 200);
        }
    }

    private void OnNoticeDismissed()
    {
        _notice?.Hide();
        _noticeActive = false;
        ShellNative.KillTimer(_host, NoticeTimerId);
        PumpNotices();
    }

    private void PumpNotices()
    {
        if (_pendingNotices.Count == 0) return;
        (string title, string text) = _pendingNotices.Dequeue();
        ShowNoticeNow(title, text);
    }

    private static bool SuppressNotifications()
    {
        if (ShellNative.SHQueryUserNotificationState(out ShellNative.QUERY_USER_NOTIFICATION_STATE state) != 0)
            return false;
        return state is ShellNative.QUERY_USER_NOTIFICATION_STATE.QuietTime
            or ShellNative.QUERY_USER_NOTIFICATION_STATE.PresentationMode
            or ShellNative.QUERY_USER_NOTIFICATION_STATE.RunningD3dFullScreen;
    }

    /// <summary>冒烟用：直接拿到自绘通知窗做确定性断言，不依赖消息循环。</summary>
    internal NoticeWindow? Notice => _notice;

    /// <summary>用 Win32 直接画 16×16 的 E「双线」三态图标（品牌资产栅格化前的占位实现）。</summary>
    private static unsafe IntPtr CreateIcon(bool active, bool dim)
    {
        const int size = 16;
        static uint Bgra(int r, int g, int b, int a = 255) =>
            ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | (uint)r;

        var header = new ShellNative.BITMAPINFOHEADER
        {
            BiSize = (uint)Marshal.SizeOf<ShellNative.BITMAPINFOHEADER>(),
            BiWidth = size,
            BiHeight = -size,
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = 0,
        };

        IntPtr screenDc = ShellNative.GetDC(IntPtr.Zero);
        IntPtr memDc = ShellNative.CreateCompatibleDC(screenDc);
        IntPtr color = ShellNative.CreateDIBSection(memDc, ref header, ShellNative.DibRgbColors,
            out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

        int alpha = dim ? 110 : 255;
        uint* pixels = (uint*)bits.ToPointer();
        new Span<uint>(pixels, size * size).Clear();
        for (int x = 2; x <= 13; x++)
        {
            pixels[4 * size + x] = Bgra(79, 91, 213, alpha);
            pixels[5 * size + x] = Bgra(79, 91, 213, alpha);
            int sag = (int)Math.Round(2 * Math.Sin(Math.PI * (x - 2) / 11.0));
            pixels[(10 + sag) * size + x] = Bgra(108, 99, 244, active ? 255 : alpha);
            pixels[(11 + sag) * size + x] = Bgra(108, 99, 244, active ? 255 : alpha);
        }
        for (int y = 6; y <= 9; y++)
            for (int x = 7; x <= 8; x++)
                pixels[y * size + x] = Bgra(239, 138, 36, alpha);

        int maskBytes = (size / 8) * size;
        IntPtr maskBits = Marshal.AllocHGlobal(maskBytes);
        Marshal.Copy(new byte[maskBytes], 0, maskBits, maskBytes);
        IntPtr mask = ShellNative.CreateBitmap(size, size, 1, 1, maskBits);
        var info = new ShellNative.ICONINFO
        {
            FIcon = 1,
            HbmMask = mask,
            HbmColor = color,
        };
        IntPtr icon = ShellNative.CreateIconIndirect(ref info);

        Marshal.FreeHGlobal(maskBits);
        ShellNative.DeleteObject(color);
        ShellNative.DeleteObject(mask);
        ShellNative.DeleteDC(memDc);
        ShellNative.ReleaseDC(IntPtr.Zero, screenDc);
        return icon;
    }

    public void Dispose()
    {
        ShellNative.KillTimer(_host, NoticeTimerId);
        _pendingNotices.Clear();
        _notice?.Dispose();
        _notice = null;
        if (IsAdded)
        {
            Modify(ShellNative.NIM_DELETE);
            IsAdded = false;
        }
        foreach (IntPtr icon in new[] { _iconDefault, _iconActive, _iconIdle })
            if (icon != IntPtr.Zero) ShellNative.DestroyIcon(icon);
        _iconDefault = _iconActive = _iconIdle = IntPtr.Zero;
        if (_host != IntPtr.Zero)
        {
            ShellNative.DestroyWindow(_host);
            _host = IntPtr.Zero;
        }
        if (s_instance == this) s_instance = null;
    }
}
