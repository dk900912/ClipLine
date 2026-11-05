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

    private static ShellNative.WndProcDelegate? s_proc;
    private static ShellTray? s_instance;

    private IntPtr _host;
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

    /// <summary>全局快捷键 Ctrl+Alt+T。</summary>
    public event Action? HotKeyPressed;

    public event Action? SessionEnding;

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
            else if (mouseMsg == ShellNative.WM_LBUTTONUP || mouseMsg == ShellNative.WM_LBUTTONDBLCLK)
                self?.LeftClicked?.Invoke();
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case ShellNative.WM_CLIPBOARDUPDATE:
                self?.ClipboardUpdated?.Invoke();
                return IntPtr.Zero;
            case ShellNative.WM_HOTKEY:
                self?.HotKeyPressed?.Invoke();
                return IntPtr.Zero;
            case ShellNative.WM_QUERYENDSESSION:
            case ShellNative.WM_ENDSESSION:
                self?.SessionEnding?.Invoke();
                return IntPtr.Zero;
            default:
                return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>首次安装的欢迎提示：系统气泡，不额外创建 XAML 窗口。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!IsAdded) return;
        var data = new ShellNative.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.NOTIFYICONDATAW>(),
            HWnd = _host,
            UID = 1,
            UFlags = ShellNative.NIF_INFO,
            SzTip = _tooltip,
            SzInfo = text.Length > 250 ? text[..250] : text,
            SzInfoTitle = title.Length > 60 ? title[..60] : title,
            DwInfoFlags = 1, // NIIF_INFO
        };
        ShellNative.Shell_NotifyIconW(ShellNative.NIM_MODIFY, ref data);
    }

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
