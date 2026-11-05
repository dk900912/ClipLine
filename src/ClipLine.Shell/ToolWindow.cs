using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>P4 四类系统替身窗口；每个都有独立外观与交互，不再共用「系统默认应用打开」。</summary>
public enum ToolMode
{
    QuickLook,
    Markup,
    Trim,
    Held,
}

/// <summary>标注画布上的一条笔迹/形状，坐标是工具窗口客户区物理像素。</summary>
public sealed record MarkShape(int Kind, int X0, int Y0, int X1, int Y1);

/// <summary>一次工具窗口重绘需要的全部状态。</summary>
public sealed record ToolFrame(
    ToolMode Mode,
    string Title,
    string Subtitle,
    string Status,
    IReadOnlyList<string> Paths,
    int Index,
    int ToolIndex,
    IReadOnlyList<MarkShape> Shapes,
    MarkShape? Preview);

/// <summary>渲染层回填的命中矩形，WndProc 用它把真实鼠标坐标翻译成按钮/画布动作。</summary>
public sealed class ToolLayout
{
    public (int X, int Y, int W, int H) Close;
    public (int X, int Y, int W, int H) Canvas;
    public (int X, int Y, int W, int H) Save;
    public (int X, int Y, int W, int H) Cancel;
    public (int X, int Y, int W, int H) PutBack;
    public (int X, int Y, int W, int H) Trim;
    public readonly List<(int X, int Y, int W, int H)> Tools = new();
    public readonly List<(int X, int Y, int W, int H)> Thumbs = new();

    public void Reset()
    {
        Close = Canvas = Save = Cancel = PutBack = Trim = default;
        Tools.Clear();
        Thumbs.Clear();
    }
}

/// <summary>组合根持有的窗口集合：同一时刻只显示一个工具窗口。</summary>
internal sealed class ToolHost : IDisposable
{
    private readonly ShellAppHost _host;
    private readonly Dictionary<ToolMode, ToolWindow> _windows = new();

    public ToolHost(ShellAppHost host) => _host = host;

    public ToolWindow? Active { get; private set; }
    public ToolWindow? Get(ToolMode mode) => _windows.TryGetValue(mode, out ToolWindow? w) ? w : null;
    public bool IsVisible(ToolMode mode) => Get(mode)?.IsVisible ?? false;
    public int WindowCount => _windows.Count;

    public bool Show(ToolMode mode, IReadOnlyList<string> paths, int index, Guid itemId, string hint = "")
    {
        foreach (var pair in _windows)
            if (pair.Key != mode) pair.Value.Hide();
        if (!_windows.TryGetValue(mode, out ToolWindow? window))
        {
            window = new ToolWindow(_host, OnWindowClosed);
            _windows[mode] = window;
        }
        Active = window;
        return window.Show(mode, paths, index, itemId, hint);
    }

    private void OnWindowClosed(ToolWindow window)
    {
        // 「拿在手上」的关闭是语义动作：门面要把 Held 复位并撤掉绳上淡影。
        if (window.Mode == ToolMode.Held && window.ItemId != Guid.Empty)
            _host.Facade.Handle(new HeldWindowCommand(HeldAction.HangBack, window.ItemId));
        if (ReferenceEquals(Active, window)) Active = null;
    }

    /// <summary>门面发来的 CloseQuickLook / CloseHeldWindow：只关窗，不再反向通知门面。</summary>
    public void Close(ToolMode mode)
    {
        if (_windows.TryGetValue(mode, out ToolWindow? window)) window.Hide();
        if (Active is not null && Active.Mode == mode) Active = null;
    }

    public void CloseAll()
    {
        foreach (ToolWindow window in _windows.Values) window.Hide();
        Active = null;
    }

    public bool DumpFrame(ToolMode mode, string path) => Get(mode)?.DumpFrame(path) ?? false;

    public void Dispose()
    {
        foreach (ToolWindow window in _windows.Values) window.Dispose();
        _windows.Clear();
        Active = null;
    }
}

/// <summary>
/// 自绘分层 HWND 工具窗口。与绳窗口不同，它<strong>不</strong>带 WS_EX_TRANSPARENT /
/// NOACTIVATE —— 需要接收真实鼠标点击与键盘。UpdateLayeredWindow 的逐像素 alpha
/// 让圆角外自动穿透，窗口内正常命中。
///
/// 四个模式共用一套 Fluent 风格 chrome（圆角 10、40 DIP 标题栏、品牌渐变竖条、
/// 26×26 关闭按钮），内容区按 docs/05 §7 分别绘制。
/// </summary>
internal sealed class ToolWindow : IDisposable
{
    public const string ClassName = "ClipLine_ToolWindow";

    private static readonly Dictionary<IntPtr, ToolWindow> Windows = new();
    private static ShellNative.WndProcDelegate? s_proc;
    private static bool s_registered;
    private static int s_heldOffset;

    private readonly ShellAppHost _host;
    private readonly Action<ToolWindow> _onClosed;
    private readonly double _scale;
    private readonly ToolLayout _layout = new();
    private readonly List<MarkShape> _shapes = new();
    private IntPtr _hwnd;
    private RopeCanvas? _canvas;
    private ToolMode _mode;
    private List<string> _paths = new();
    private int _index;
    private int _toolIndex;
    private Guid _itemId;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _status = string.Empty;
    private bool _dragging;
    private int _dx0, _dy0, _dx1, _dy1;
    private int _posX, _posY;
    private bool _disposed;

    public ToolWindow(ShellAppHost host, Action<ToolWindow> onClosed)
    {
        _host = host;
        _onClosed = onClosed;
        _scale = host.Scale <= 0 ? 1.0 : host.Scale;
    }

    public ToolMode Mode => _mode;
    public Guid ItemId => _itemId;
    public string Status => _status;
    public int ShapeCount => _shapes.Count;
    public int Index => _index;
    public IReadOnlyList<MarkShape> Shapes => _shapes;
    public ToolLayout Layout => _layout;
    public IntPtr Handle => _hwnd;
    public IReadOnlyList<string> Paths => _paths;
    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    public static (int W, int H) SizeFor(ToolMode mode) => mode switch
    {
        ToolMode.QuickLook => (720, 480),
        ToolMode.Markup => (860, 600),
        ToolMode.Trim => (720, 520),
        _ => (520, 400),
    };

    private static string TitleFor(ToolMode mode) => mode switch
    {
        ToolMode.QuickLook => "快速查看",
        ToolMode.Markup => "标注",
        ToolMode.Trim => "修剪录屏",
        _ => "拿在手上",
    };

    private static string DefaultStatus(ToolMode mode) => mode switch
    {
        ToolMode.QuickLook => "← → 浏览整条绳 · 空格 / Esc 关闭",
        ToolMode.Markup => "在图上拖拽即可添加标注 · 保存会导出一份 PNG 副本并挂上绳",
        ToolMode.Trim => "v1 未接入视频编码；录屏预览与修剪条可用，导出请交给系统播放器",
        _ => "Esc 或「放回绳上」挂回绳子 · Ctrl+C 复制",
    };

    private static void EnsureClass()
    {
        if (s_registered) return;
        s_proc = WndProc;
        var wc = new ShellNative.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            HInstance = ShellNative.GetModuleHandleW(null),
            HCursor = ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcArrow),
            LpszClassName = ClassName,
        };
        if (ShellNative.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("注册工具窗口类失败，Win32Error=" +
                Marshal.GetLastWin32Error());
        s_registered = true;
    }

    public bool Show(ToolMode mode, IReadOnlyList<string> paths, int index, Guid itemId, string hint)
    {
        EnsureClass();
        _mode = mode;
        _paths = new List<string>(paths);
        _index = _paths.Count == 0 ? 0 : Math.Clamp(index, 0, _paths.Count - 1);
        _itemId = itemId;
        _shapes.Clear();
        _toolIndex = 0;
        _dragging = false;
        _title = TitleFor(mode);
        _subtitle = _paths.Count > 0 ? Path.GetFileName(_paths[_index]) : string.Empty;
        _status = hint.Length > 0 ? hint : DefaultStatus(mode);

        (int wDip, int hDip) = SizeFor(mode);
        int w = Px(wDip), h = Px(hDip);
        if (!EnsureWindow(w, h)) return false;
        Position(w, h);
        Render();
        ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOW);
        ShellNative.SetForegroundWindow(_hwnd);
        ShellNative.SetFocus(_hwnd);
        return true;
    }

    private bool EnsureWindow(int w, int h)
    {
        if (!Exists)
        {
            // 不带 TRANSPARENT / NOACTIVATE：工具窗口必须能接收鼠标与键盘。
            uint exStyle = ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_TOOLWINDOW;
            _hwnd = ShellNative.CreateWindowExW(exStyle, ClassName, "ClipLine tool",
                ShellNative.WS_POPUP, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero,
                ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            Windows[_hwnd] = this;
        }
        ShellNative.SetWindowPos(_hwnd, ShellNative.HwndTopmost, 0, 0, w, h,
            ShellNative.SWP_NOMOVE | ShellNative.SWP_NOACTIVATE);
        if (_canvas is null || _canvas.WidthPx != w || _canvas.HeightPx != h)
        {
            _canvas?.Dispose();
            _canvas = new RopeCanvas(w, h, _scale);
        }
        return true;
    }

    private void Position(int w, int h)
    {
        var area = new ShellNative.RECT();
        if (!ShellNative.SystemParametersInfoW(ShellNative.SPI_GETWORKAREA, 0, ref area, 0)
            || area.Right <= area.Left || area.Bottom <= area.Top)
        {
            area.Left = 0;
            area.Top = 0;
            area.Right = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
            area.Bottom = ShellNative.GetSystemMetrics(ShellNative.SM_CYSCREEN);
        }
        int x = area.Left + (area.Right - area.Left - w) / 2;
        int y = area.Top + (area.Bottom - area.Top - h) / 2;
        if (_mode == ToolMode.Held)
        {
            int step = (s_heldOffset++ % 8) * Px(24);
            x = Math.Min(x + step, area.Right - w - Px(8));
            y = Math.Min(y + step, area.Bottom - h - Px(8));
        }
        _posX = x;
        _posY = y;
        ShellNative.SetWindowPos(_hwnd, ShellNative.HwndTopmost, x, y, 0, 0,
            ShellNative.SWP_NOSIZE | ShellNative.SWP_NOACTIVATE);
    }

    private void Render()
    {
        if (_canvas is null || !Exists) return;
        MarkShape? preview = _dragging
            ? new MarkShape(_toolIndex, _dx0, _dy0, _dx1, _dy1)
            : null;
        var frame = new ToolFrame(_mode, _title, _subtitle, _status,
            _paths, _index, _toolIndex, _shapes, preview);
        _canvas.RenderTool(frame, _layout);
        _canvas.Present(_hwnd, 255, _posX, _posY);
    }

    public bool DumpFrame(string path) => _canvas?.SavePng(path) ?? false;

    /// <summary>导出标注画布（含形状）为 PNG 副本；v1 不覆盖用户原图。</summary>
    public bool DumpCanvas(string path)
    {
        if (_canvas is null) return false;
        var c = _layout.Canvas;
        if (c.W <= 2 || c.H <= 2) return false;
        return _canvas.SavePng(path, c.X + 1, c.Y + 1, c.W - 2, c.H - 2);
    }

    public void Hide()
    {
        if (Exists) ShellNative.ShowWindow(_hwnd, ShellNative.SW_HIDE);
        _dragging = false;
    }

    private void Close(bool notify)
    {
        Hide();
        if (notify) _onClosed(this);
    }

    private void SaveMarkup()
    {
        if (_paths.Count == 0) return;
        string source = _paths[_index];
        string dir = Path.GetDirectoryName(source) ?? Path.GetTempPath();
        string output = Path.Combine(dir,
            Path.GetFileNameWithoutExtension(source) + "-已标注.png");
        bool ok = DumpCanvas(output);
        _status = ok
            ? "已保存标注副本：" + Path.GetFileName(output)
            : "保存失败：画布无法导出";
        if (ok)
        {
            // 把成果挂上绳，用户能立刻看到「标记」真的产生了新东西。
            _host.Facade.Handle(new NewItem(output, ItemOrigin.ShellExtension,
                ByHand: true, InOwnDirectory: true));
            _host.RenderFrame();
        }
        Render();
    }

    private void Step(int delta)
    {
        if (_paths.Count == 0) return;
        _index = (_index + delta + _paths.Count) % _paths.Count;
        _subtitle = Path.GetFileName(_paths[_index]);
        Render();
    }

    private static bool In((int X, int Y, int W, int H) r, int x, int y) =>
        r.W > 0 && r.H > 0 && x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    private static (int X, int Y) Point(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        return ((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == ShellNative.WM_SETCURSOR)
        {
            ShellNative.SetCursor(ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcArrow));
            return new IntPtr(1);
        }
        if (!Windows.TryGetValue(hWnd, out ToolWindow? window)) 
            return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case ShellNative.WM_LBUTTONDOWN:
            {
                (int x, int y) = Point(lParam);
                if (In(window._layout.Canvas, x, y) && window._mode == ToolMode.Markup)
                {
                    window._dragging = true;
                    window._dx0 = window._dx1 = x;
                    window._dy0 = window._dy1 = y;
                    window.Render();
                }
                return IntPtr.Zero;
            }
            case ShellNative.WM_MOUSEMOVE:
            {
                if (!window._dragging || (wParam.ToUInt64() & ShellNative.MkLButton) == 0)
                    return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
                (int x, int y) = Point(lParam);
                if (x == window._dx1 && y == window._dy1) return IntPtr.Zero;
                window._dx1 = x;
                window._dy1 = y;
                window.Render();
                return IntPtr.Zero;
            }
            case ShellNative.WM_LBUTTONUP:
            {
                (int x, int y) = Point(lParam);
                if (window._dragging)
                {
                    window._dragging = false;
                    if (Math.Abs(x - window._dx0) + Math.Abs(y - window._dy0) > 3)
                        window._shapes.Add(new MarkShape(window._toolIndex,
                            window._dx0, window._dy0, x, y));
                    window.Render();
                    return IntPtr.Zero;
                }
                if (In(window._layout.Close, x, y)) { window.Close(true); return IntPtr.Zero; }
                for (int i = 0; i < window._layout.Tools.Count; i++)
                    if (In(window._layout.Tools[i], x, y))
                    {
                        window._toolIndex = i;
                        window.Render();
                        return IntPtr.Zero;
                    }
                for (int i = 0; i < window._layout.Thumbs.Count; i++)
                    if (In(window._layout.Thumbs[i], x, y))
                    {
                        window._index = Math.Min(i, window._paths.Count - 1);
                        window._subtitle = window._paths.Count > 0
                            ? Path.GetFileName(window._paths[window._index]) : string.Empty;
                        window.Render();
                        return IntPtr.Zero;
                    }
                if (In(window._layout.Save, x, y)) { window.SaveMarkup(); return IntPtr.Zero; }
                if (In(window._layout.Cancel, x, y) || In(window._layout.PutBack, x, y))
                {
                    window.Close(true);
                    return IntPtr.Zero;
                }
                if (In(window._layout.Trim, x, y))
                {
                    window._status = "v1 未接入视频编码：修剪范围已记录，导出请交给系统播放器";
                    window.Render();
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;
            }
            case ShellNative.WM_KEYDOWN:
            {
                uint vk = (uint)wParam.ToUInt64();
                if (vk == ShellNative.VkEscape || vk == ShellNative.VkSpace)
                {
                    window.Close(true);
                    return IntPtr.Zero;
                }
                if (vk == ShellNative.VkLeft) { window.Step(-1); return IntPtr.Zero; }
                if (vk == ShellNative.VkRight) { window.Step(1); return IntPtr.Zero; }
                if (vk == ShellNative.VkC && (ShellNative.GetKeyState(0x11) & 0x8000) != 0
                    && window._mode == ToolMode.Held && window._itemId != Guid.Empty)
                {
                    window._host.Facade.Handle(new HeldWindowCommand(HeldAction.Copy, window._itemId));
                    window._status = "已复制（Ctrl+C）";
                    window.Render();
                    return IntPtr.Zero;
                }
                return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
            }
            case ShellNative.WM_CLOSE:
                window.Close(true);
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
