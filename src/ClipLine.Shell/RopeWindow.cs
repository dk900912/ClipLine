using System.Runtime.InteropServices;
using ClipLine.Domain;

namespace ClipLine.Shell;

public enum RopeMouseKind
{
    Down,
    Up,
    DoubleClick,
    RightClick,
    Wheel,
}

/// <summary>卡片区鼠标事件；坐标是窗口客户区物理像素。</summary>
public readonly record struct RopeMouseEvent(
    RopeMouseKind Kind, int X, int Y, int CardIndex, Guid ItemId, int WheelDelta);

/// <summary>
/// 自管分层 HWND 绳窗口（P0 结论的落地）：
/// WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，初始 WS_EX_TRANSPARENT 整窗穿透，
/// 指针落在卡片上时清除 TRANSPARENT + SWP_FRAMECHANGED 做局部命中。
/// v1 起窗口过程把卡片点击 / 右键 / 滚轮分发给真实输入桥，映射到应用门面的离散事件。
/// </summary>
public sealed class RopeWindow : IDisposable
{
    public const string ClassName = "ClipLine_RopeWindow";

    private static readonly Dictionary<IntPtr, RopeWindow> Windows = new();
    private static ShellNative.WndProcDelegate? s_proc;
    private static bool s_classRegistered;

    private readonly double _scale;
    private RopeCanvas? _canvas;
    private IntPtr _hwnd;
    private bool _transparent = true;
    private RopeFrame? _frame;

    public RopeWindow(double scale) => _scale = scale <= 0 ? 1.0 : scale;

    public IntPtr Handle => _hwnd;
    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);
    public IReadOnlyList<(int X, int Y, int W, int H)> CardRects =>
        _canvas?.CardRects ?? Array.Empty<(int, int, int, int)>();

    /// <summary>真实鼠标输入（已换算成卡片索引与条目 Id）。</summary>
    public event Action<RopeMouseEvent>? Mouse;

    private static void EnsureClass()
    {
        if (s_classRegistered) return;
        s_proc = WndProc; // 保活，防 GC
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

    /// <summary>按门面 PresentWindow 命令展示；卡片内容取自完整状态帧。</summary>
    public bool Present(RopeFrame frame)
    {
        EnsureClass();
        _frame = frame;
        int widthPx = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
        int heightPx = (int)Math.Round(Layout.PanelHeight(frame.Size) * _scale);

        if (!Exists)
        {
            uint exStyle = ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_TRANSPARENT
                | ShellNative.WS_EX_NOACTIVATE | ShellNative.WS_EX_TOOLWINDOW;
            _hwnd = ShellNative.CreateWindowExW(exStyle, ClassName, "ClipLine rope", ShellNative.WS_POPUP,
                0, 0, widthPx, heightPx, IntPtr.Zero, IntPtr.Zero,
                ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            Windows[_hwnd] = this;
            ShellNative.SetWindowPos(_hwnd, ShellNative.HwndTopmost, 0, 0, 0, 0,
                ShellNative.SWP_NOMOVE | ShellNative.SWP_NOSIZE | ShellNative.SWP_NOACTIVATE);
            _transparent = true;
        }

        if (_canvas is null || _canvas.WidthPx != widthPx || _canvas.HeightPx != heightPx)
        {
            _canvas?.Dispose();
            _canvas = new RopeCanvas(widthPx, heightPx, _scale);
        }

        _canvas.Render(frame);
        if (!_canvas.Present(_hwnd)) return false;
        ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOWNOACTIVATE);
        return true;
    }

    /// <summary>指针是否落在任一卡片区（物理像素坐标）。</summary>
    public bool HitTest(int px, int py) => HitCardIndex(px, py) >= 0;

    private int HitCardIndex(int px, int py)
    {
        if (_canvas is null) return -1;
        int pad = (int)Math.Round(Layout.HitPadding * _scale);
        var rects = _canvas.CardRects;
        for (int i = 0; i < rects.Count; i++)
        {
            var (x, y, w, h) = rects[i];
            if (px >= x - pad && px <= x + w + pad && py >= y - pad && py <= y + h + pad) return i;
        }
        return -1;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hWnd, out RopeWindow? window))
        {
            long packed = lParam.ToInt64();
            int x = (short)(packed & 0xFFFF);
            int y = (short)((packed >> 16) & 0xFFFF);
            int wheel = (short)((wParam.ToUInt64() >> 16) & 0xFFFF);
            RopeMouseKind? kind = msg switch
            {
                ShellNative.WM_LBUTTONDOWN => RopeMouseKind.Down,
                ShellNative.WM_LBUTTONUP => RopeMouseKind.Up,
                ShellNative.WM_LBUTTONDBLCLK => RopeMouseKind.DoubleClick,
                ShellNative.WM_RBUTTONUP => RopeMouseKind.RightClick,
                ShellNative.WM_MOUSEWHEEL => RopeMouseKind.Wheel,
                _ => (RopeMouseKind?)null,
            };
            if (kind is not null)
            {
                Guid id = Guid.Empty;
                int index = window.HitCardIndex(x, y);
                if (index >= 0 && window._frame is not null && index < window._frame.Cards.Count)
                    id = window._frame.Cards[index].Id;
                var evt = new RopeMouseEvent(kind.Value, x, y, index, id, wheel);
                Post(() => window.Mouse?.Invoke(evt));
                return IntPtr.Zero;
            }
        }
        return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>异步派发，避免在窗口过程里重入 UpdateLayeredWindow / TrackPopupMenu。</summary>
    private static void Post(Action action)
    {
        SynchronizationContext? context = SynchronizationContext.Current;
        if (context is null) action();
        else context.Post(static state => ((Action)state!)(), action);
    }

    /// <summary>动态切换整窗穿透；P0 已验证 SetWindowPos(SWP_FRAMECHANGED) 使其立即生效。</summary>
    public void SetPassthrough(bool passthrough)
    {
        if (!Exists || _transparent == passthrough) return;
        long style = ShellNative.GetWindowLongPtrW(_hwnd, ShellNative.GWL_EXSTYLE).ToInt64();
        long updated = passthrough ? style | ShellNative.WS_EX_TRANSPARENT : style & ~ShellNative.WS_EX_TRANSPARENT;
        ShellNative.SetWindowLongPtrW(_hwnd, ShellNative.GWL_EXSTYLE, new IntPtr(updated));
        // 命中切换时重新置顶：别的顶层窗口（气泡、浮动工具条）可能盖在卡片上，
        // 既影响可见性也会让 WindowFromPoint 命中错误窗口。
        ShellNative.SetWindowPos(_hwnd, ShellNative.HwndTopmost, 0, 0, 0, 0,
            ShellNative.SWP_NOMOVE | ShellNative.SWP_NOSIZE
            | ShellNative.SWP_NOACTIVATE | ShellNative.SWP_FRAMECHANGED);
        _transparent = passthrough;
    }

    public bool IsPassthrough => _transparent;

    /// <summary>导出当前渲染帧（真实缩略图内容），不依赖屏幕抓屏。</summary>
    public bool DumpFrame(string path) => _canvas?.SavePng(path) ?? false;

    /// <summary>门面收起（SetHitTest=passthrough）：立即隐藏。</summary>
    public void Hide()
    {
        if (Exists) ShellNative.ShowWindow(_hwnd, ShellNative.SW_HIDE);
    }

    /// <summary>门面 DestroyWindow 命令：收起 0.3 s 后销毁，不留窗口（P0 自检项）。</summary>
    public void DestroyNow()
    {
        if (_hwnd == IntPtr.Zero) return;
        Windows.Remove(_hwnd);
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
