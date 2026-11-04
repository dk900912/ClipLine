using System.Runtime.InteropServices;
using ClipLine.Domain;
using ClipLine.Platform;

namespace ClipLine.Shell;

/// <summary>
/// 自管分层 HWND 绳窗口（产品版，P0 结论的落地）：
/// WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，初始 WS_EX_TRANSPARENT 整窗穿透，
/// 指针落在卡片上时清除 TRANSPARENT + SWP_FRAMECHANGED 做局部命中，收起 0.3 s 后由门面发 DestroyWindow。
/// </summary>
public sealed class RopeWindow : IDisposable
{
    public const string ClassName = "ClipLine_RopeWindow";

    private static ShellNative.WndProcDelegate? s_proc;
    private static bool s_classRegistered;

    private readonly double _scale;
    private RopeCanvas? _canvas;
    private IntPtr _hwnd;
    private bool _transparent = true;

    public RopeWindow(double scale) => _scale = scale <= 0 ? 1.0 : scale;

    public IntPtr Handle => _hwnd;
    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);
    public IReadOnlyList<(int X, int Y, int W, int H)> CardRects =>
        _canvas?.CardRects ?? Array.Empty<(int, int, int, int)>();

    private static void EnsureClass()
    {
        if (s_classRegistered) return;
        s_proc = static (h, m, w, l) => ShellNative.DefWindowProcW(h, m, w, l);
        var wc = new ShellNative.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            HInstance = ShellNative.GetModuleHandleW(null),
            LpszClassName = ClassName,
        };
        if (ShellNative.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("注册绳窗口类失败，Win32Error=" + Marshal.GetLastWin32Error());
        s_classRegistered = true;
    }

    /// <summary>按门面 PresentWindow 命令展示；卡片数与尺寸取自状态视图。</summary>
    public bool Present(StateSnapshotView view)
    {
        EnsureClass();
        int widthPx = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
        int heightPx = (int)Math.Round(Layout.PanelHeight(view.Size) * _scale);

        if (!Exists)
        {
            uint exStyle = ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_TRANSPARENT
                | ShellNative.WS_EX_NOACTIVATE | ShellNative.WS_EX_TOOLWINDOW;
            _hwnd = ShellNative.CreateWindowExW(exStyle, ClassName, "ClipLine rope", ShellNative.WS_POPUP,
                0, 0, widthPx, heightPx, IntPtr.Zero, IntPtr.Zero,
                ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            ShellNative.SetWindowPos(_hwnd, ShellNative.HwndTopmost, 0, 0, 0, 0,
                ShellNative.SWP_NOMOVE | ShellNative.SWP_NOSIZE | ShellNative.SWP_NOACTIVATE);
            _transparent = true;
        }

        if (_canvas is null || _canvas.WidthPx != widthPx || _canvas.HeightPx != heightPx)
        {
            _canvas?.Dispose();
            _canvas = new RopeCanvas(widthPx, heightPx, _scale);
        }

        _canvas.Render(view);
        if (!_canvas.Present(_hwnd)) return false;
        ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOWNOACTIVATE);
        return true;
    }

    /// <summary>指针是否落在任一卡片区（物理像素坐标）。</summary>
    public bool HitTest(int px, int py)
    {
        foreach (var (x, y, w, h) in CardRects)
        {
            int pad = (int)Math.Round(Layout.HitPadding * _scale);
            if (px >= x - pad && px <= x + w + pad && py >= y - pad && py <= y + h + pad) return true;
        }
        return false;
    }

    /// <summary>动态切换整窗穿透；P0 已验证 SetWindowPos(SWP_FRAMECHANGED) 使其立即生效。</summary>
    public void SetPassthrough(bool passthrough)
    {
        if (!Exists || _transparent == passthrough) return;
        long style = ShellNative.GetWindowLongPtrW(_hwnd, ShellNative.GWL_EXSTYLE).ToInt64();
        long updated = passthrough ? style | ShellNative.WS_EX_TRANSPARENT : style & ~ShellNative.WS_EX_TRANSPARENT;
        ShellNative.SetWindowLongPtrW(_hwnd, ShellNative.GWL_EXSTYLE, new IntPtr(updated));
        ShellNative.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            ShellNative.SWP_NOMOVE | ShellNative.SWP_NOSIZE | ShellNative.SWP_NOZORDER
            | ShellNative.SWP_NOACTIVATE | ShellNative.SWP_FRAMECHANGED);
        _transparent = passthrough;
    }

    public bool IsPassthrough => _transparent;

    /// <summary>门面收起（SetHitTest=passthrough）：立即隐藏；滑上动画由将来的 Win2D 路径承担。</summary>
    public void Hide()
    {
        if (Exists) ShellNative.ShowWindow(_hwnd, ShellNative.SW_HIDE);
    }

    /// <summary>门面 DestroyWindow 命令：收起 0.3 s 后销毁，不留窗口（P0 自检项）。</summary>
    public void DestroyNow()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (ShellNative.IsWindow(_hwnd)) ShellNative.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _transparent = true;
    }

    public void Dispose()
    {
        DestroyNow();
        _canvas?.Dispose();
        _canvas = null;
    }
}
