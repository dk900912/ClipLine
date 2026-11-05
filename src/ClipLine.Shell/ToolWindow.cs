using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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

/// <summary>鼠标停在哪个可交互元素上；绘制层据此给 hover / pressed 态。</summary>
public enum ToolHover
{
    None,
    Close,
    Pin,
    Save,
    Cancel,
    Primary,
    Tool,
    Thumb,
    ConfirmSave,
    ConfirmDiscard,
    ConfirmCancel,
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
    MarkShape? Preview,
    bool ConfirmClose = false,
    ToolHover Hover = ToolHover.None,
    int HoverIndex = -1,
    bool Pressed = false,
    bool Pinned = false,
    double Zoom = 1.0,
    int PanX = 0,
    int PanY = 0,
    bool SpaceHeld = false);

/// <summary>渲染层回填的命中矩形，WndProc 用它把真实鼠标坐标翻译成按钮/画布动作。</summary>
public sealed class ToolLayout
{
    public (int X, int Y, int W, int H) Close;
    public (int X, int Y, int W, int H) Pin;
    public (int X, int Y, int W, int H) Canvas;
    public (int X, int Y, int W, int H) Save;
    public (int X, int Y, int W, int H) Cancel;
    public (int X, int Y, int W, int H) PutBack;
    public (int X, int Y, int W, int H) Trim;
    public (int X, int Y, int W, int H) ConfirmSave;
    public (int X, int Y, int W, int H) ConfirmDiscard;
    public (int X, int Y, int W, int H) ConfirmCancel;
    public readonly List<(int X, int Y, int W, int H)> Tools = new();
    public readonly List<(int X, int Y, int W, int H)> Thumbs = new();
    public readonly List<int> ThumbIndexes = new();

    public void Reset()
    {
        Close = Pin = Canvas = Save = Cancel = PutBack = Trim = default;
        ConfirmSave = ConfirmDiscard = ConfirmCancel = default;
        Tools.Clear();
        Thumbs.Clear();
        ThumbIndexes.Clear();
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

    /// <summary>是否有标注窗带着未保存内容（更新安装前据此延后，见 P0-3）。</summary>
    public bool HasUnsavedMarkup => Get(ToolMode.Markup) is { IsVisible: true, IsDirty: true };

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

    /// <summary>系统主题变化：所有已建窗口立即换色重绘。</summary>
    public void ApplyTheme()
    {
        foreach (ToolWindow window in _windows.Values) window.ApplyTheme();
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
/// v1.6 起具备真正的桌面窗口行为（docs/11 P0-1）：可拖标题栏、八向缩放、双击最大化、
/// 进任务栏与 Alt+Tab、非默认置顶（图钉可切）；「拿在手上」保留浮窗语义。
/// </summary>
internal sealed class ToolWindow : IDisposable
{
    public const string ClassName = "ClipLine_ToolWindow";

    private const int MinWidthDip = 520;
    private const int MinHeightDip = 360;
    private const int ResizeBorderDip = 6;
    private const int TitleHeightDip = 44;
    private const int MaxUndo = 30;

    private static readonly Dictionary<IntPtr, ToolWindow> Windows = new();
    private static ShellNative.WndProcDelegate? s_proc;
    private static bool s_registered;
    private static int s_heldOffset;

    private readonly ShellAppHost _host;
    private readonly Action<ToolWindow> _onClosed;
    private readonly ToolLayout _layout = new();
    private readonly List<MarkShape> _shapes = new();
    private readonly List<List<MarkShape>> _undo = new();
    private readonly List<List<MarkShape>> _redo = new();
    private double _scale;
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
    private string _shapesPath = string.Empty;
    private bool _dragging;
    private bool _spaceDown;
    private int _dx0, _dy0, _dx1, _dy1;
    private int _downX, _downY;
    private int _panX, _panY;
    private double _zoom = 1.0;
    private int _posX, _posY;
    private bool _dirty;
    private bool _confirmClose;
    private bool _pinned;
    private bool _maximized;
    private ShellNative.RECT _restoreRect;
    private ToolHover _hover;
    private int _hoverIndex = -1;
    private bool _pressed;
    private DragMode _drag;
    private ShellNative.POINT _dragStartCursor;
    private ShellNative.RECT _dragStartRect;
    private long _lastResizeTick;
    private bool _disposed;

    private enum DragMode
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Markup,
        Pan,
    }

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
    public bool IsDirty => _dirty;
    public bool IsConfirming => _confirmClose;
    public bool IsPinned => _pinned;
    public bool IsMaximized => _maximized;
    public IReadOnlyList<MarkShape> Shapes => _shapes;
    public ToolLayout Layout => _layout;
    public IntPtr Handle => _hwnd;
    public IReadOnlyList<string> Paths => _paths;
    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    public static (int W, int H) SizeFor(ToolMode mode) => mode switch
    {
        ToolMode.QuickLook => (760, 520),
        ToolMode.Markup => (900, 640),
        ToolMode.Trim => (760, 560),
        _ => (520, 400),
    };

    private static string TitleFor(ToolMode mode) => mode switch
    {
        ToolMode.QuickLook => "快速查看",
        ToolMode.Markup => "标注",
        ToolMode.Trim => "录屏预览",
        _ => "拿在手上",
    };

    private static string DefaultStatus(ToolMode mode) => mode switch
    {
        ToolMode.QuickLook => "← → 浏览整条绳 · Ctrl+滚轮 缩放 · 空格 / Esc 关闭",
        ToolMode.Markup => "拖拽绘制 · Ctrl+Z 撤销 · Ctrl+S 保存 · 空格+拖拽 平移",
        ToolMode.Trim => "预览录屏 · 导出请用系统播放器",
        _ => "Esc 或「放回绳上」挂回绳子 · Ctrl+C 复制",
    };

    private static void EnsureClass()
    {
        if (s_registered) return;
        s_proc = WndProc;
        var wc = new ShellNative.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.WNDCLASSEXW>(),
            // CS_DBLCLKS：标题栏双击最大化必须有 WM_LBUTTONDBLCLK。
            Style = 0x0008,
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
        _toolIndex = 0;
        _dragging = false;
        _spaceDown = false;
        _confirmClose = false;
        _hover = ToolHover.None;
        _hoverIndex = -1;
        _pressed = false;
        _drag = DragMode.None;
        _title = TitleFor(mode);
        _subtitle = _paths.Count > 0 ? Path.GetFileName(_paths[_index]) : string.Empty;
        _status = hint.Length > 0 ? hint : DefaultStatus(mode);

        string source = _paths.Count > 0 ? _paths[_index] : string.Empty;
        if (mode == ToolMode.Markup)
        {
            if (!string.Equals(source, _shapesPath, StringComparison.OrdinalIgnoreCase))
            {
                _shapes.Clear();
                _undo.Clear();
                _redo.Clear();
                _dirty = false;
                _shapesPath = source;
                RestoreDraft(source);
            }
        }
        else
        {
            _shapes.Clear();
            _undo.Clear();
            _redo.Clear();
            _dirty = false;
            _shapesPath = string.Empty;
        }

        (int wDip, int hDip) = SizeFor(mode);
        int w = Px(wDip), h = Px(hDip);
        bool first = !Exists;
        if (!EnsureWindow(w, h)) return false;
        if (first)
        {
            Position(w, h);
        }
        else
        {
            // 再次打开沿用上次的位置与大小（用户拖过/缩放过就不该被重置）。
            var rect = CurrentRect();
            _posX = rect.Left;
            _posY = rect.Top;
        }
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
            // 非「拿在手上」的窗口要出现在任务栏与 Alt+Tab：WS_EX_APPWINDOW 而非 TOOLWINDOW。
            uint exStyle = ShellNative.WS_EX_LAYERED
                | (_mode == ToolMode.Held ? ShellNative.WS_EX_TOOLWINDOW : ShellNative.WS_EX_APPWINDOW);
            _hwnd = ShellNative.CreateWindowExW(exStyle, ClassName,
                "ClipLine — " + TitleFor(_mode), ShellNative.WS_POPUP, 0, 0, w, h,
                IntPtr.Zero, IntPtr.Zero, ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            Windows[_hwnd] = this;
            WindowChrome.Apply(_hwnd); // P2-6 圆角 + 深色边框
            _pinned = _mode == ToolMode.Held;
        }
        else
        {
            // 已存在的窗口可能被用户缩放/最大化过，画布按真实窗口尺寸重建。
            var rect = CurrentRect();
            w = Math.Max(1, rect.Right - rect.Left);
            h = Math.Max(1, rect.Bottom - rect.Top);
        }
        if (_canvas is null || _canvas.WidthPx != w || _canvas.HeightPx != h)
        {
            _canvas?.Dispose();
            _canvas = new RopeCanvas(w, h, _scale) { Theme = ThemePalette.Current };
        }
        ApplyZOrder();
        return true;
    }

    private void ApplyZOrder()
    {
        IntPtr after = _pinned ? ShellNative.HwndTopmost : IntPtr.Zero; // IntPtr.Zero = HWND_TOP
        ShellNative.SetWindowPos(_hwnd, after, 0, 0, 0, 0,
            ShellNative.SWP_NOMOVE | ShellNative.SWP_NOSIZE | ShellNative.SWP_NOACTIVATE);
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
        ShellNative.SetWindowPos(_hwnd, _pinned ? ShellNative.HwndTopmost : IntPtr.Zero,
            x, y, w, h, ShellNative.SWP_NOACTIVATE);
    }

    private void Render()
    {
        if (_canvas is null || !Exists) return;
        MarkShape? preview = _dragging
            ? new MarkShape(_toolIndex, _dx0, _dy0, _dx1, _dy1)
            : null;
        var frame = new ToolFrame(_mode, _title, _subtitle, _status,
            _paths, _index, _toolIndex, _shapes, preview,
            _confirmClose, _hover, _hoverIndex, _pressed, _pinned,
            _zoom, _panX, _panY, _spaceDown);
        _canvas.Theme = ThemePalette.Current;
        _canvas.RenderTool(frame, _layout);
        _canvas.Present(_hwnd, 255, _posX, _posY);
    }

    /// <summary>系统主题变化：换色并重绘当前帧。</summary>
    public void ApplyTheme()
    {
        if (_canvas is null) return;
        _canvas.Theme = ThemePalette.Current;
        Render();
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
        _drag = DragMode.None;
        ShellNative.ReleaseCapture();
    }

    private void CloseNow(bool notify)
    {
        Hide();
        _confirmClose = false;
        if (notify) _onClosed(this);
    }

    /// <summary>所有关闭入口都走这里：标注有未保存内容时先弹三选一（P0-2）。</summary>
    private void RequestClose(bool notify)
    {
        if (_mode == ToolMode.Markup && _dirty)
        {
            _confirmClose = true;
            _hover = ToolHover.None;
            _hoverIndex = -1;
            _pressed = false;
            Render();
            return;
        }
        CloseNow(notify);
    }

    private void SaveMarkup()
    {
        if (_paths.Count == 0) return;
        string source = _paths[_index];
        string dir = Path.GetDirectoryName(source) ?? Path.GetTempPath();
        string output = UniqueOutputPath(Path.Combine(dir,
            Path.GetFileNameWithoutExtension(source) + "-已标注.png"));
        bool ok = DumpCanvas(output);
        _status = ok
            ? "已保存标注副本：" + Path.GetFileName(output)
            : "保存失败：画布无法导出";
        if (ok)
        {
            _dirty = false;
            _confirmClose = false;
            DeleteDraft();
            // 把成果挂上绳，用户能立刻看到「标记」真的产生了新东西。
            _host.Facade.Handle(new NewItem(output, ItemOrigin.ShellExtension,
                ByHand: true, InOwnDirectory: true));
            _host.RenderFrame();
        }
        Render();
    }

    /// <summary>不静默覆盖：同名时用「(2)」「(3)」递增。</summary>
    private static string UniqueOutputPath(string wanted)
    {
        if (!File.Exists(wanted)) return wanted;
        string dir = Path.GetDirectoryName(wanted) ?? Path.GetTempPath();
        string stem = Path.GetFileNameWithoutExtension(wanted);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}).png");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}-{DateTime.Now:HHmmss}.png");
    }

    private void Step(int delta)
    {
        if (_paths.Count == 0) return;
        _index = (_index + delta + _paths.Count) % _paths.Count;
        _subtitle = Path.GetFileName(_paths[_index]);
        _zoom = 1.0;
        _panX = _panY = 0;
        Render();
    }

    private void SetZoom(double zoom, int anchorX, int anchorY)
    {
        double next = Math.Clamp(Math.Round(zoom, 2), 0.5, 4.0);
        if (Math.Abs(next - _zoom) < 0.001) return;
        var c = _layout.Canvas;
        // 以画布中心为锚点缩放，平移量按比例回收，避免图片飞出可视区。
        _panX = (int)Math.Round(_panX * (next / _zoom));
        _panY = (int)Math.Round(_panY * (next / _zoom));
        _zoom = next;
        ClampPan();
        Render();
    }

    private void ClampPan()
    {
        var c = _layout.Canvas;
        if (c.W <= 0 || c.H <= 0) return;
        int maxX = (int)Math.Max(0, (c.W * _zoom - c.W) / 2);
        int maxY = (int)Math.Max(0, (c.H * _zoom - c.H) / 2);
        _panX = Math.Clamp(_panX, -maxX, maxX);
        _panY = Math.Clamp(_panY, -maxY, maxY);
    }

    private void PushUndo()
    {
        _undo.Add(new List<MarkShape>(_shapes));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        _dirty = true;
        SaveDraft();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(new List<MarkShape>(_shapes));
        _shapes.Clear();
        _shapes.AddRange(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        _dirty = true;
        _status = "已撤销";
        SaveDraft();
        Render();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(new List<MarkShape>(_shapes));
        _shapes.Clear();
        _shapes.AddRange(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _dirty = true;
        _status = "已重做";
        SaveDraft();
        Render();
    }

    private static bool In((int X, int Y, int W, int H) r, int x, int y) =>
        r.W > 0 && r.H > 0 && x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    private static (int X, int Y) Point(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        return ((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    private static (int X, int Y) Cursor()
    {
        ShellNative.GetCursorPos(out ShellNative.POINT p);
        return (p.X, p.Y);
    }

    private ShellNative.RECT CurrentRect()
    {
        ShellNative.GetWindowRect(_hwnd, out ShellNative.RECT r);
        return r;
    }

    /// <summary>标题栏（排除关闭/图钉按钮）是拖动区；四周 6 DIP 是缩放区。</summary>
    private DragMode HitDragZone(int x, int y)
    {
        var rect = CurrentRect();
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        int border = Px(ResizeBorderDip);
        int pad = Px(8);
        int titleTop = pad;
        int titleBottom = pad + Px(TitleHeightDip);

        bool left = x < border, right = x > w - border;
        bool top = y < border, bottom = y > h - border;
        if (left && top) return DragMode.TopLeft;
        if (right && top) return DragMode.TopRight;
        if (left && bottom) return DragMode.BottomLeft;
        if (right && bottom) return DragMode.BottomRight;
        if (left) return DragMode.Left;
        if (right) return DragMode.Right;
        if (top) return DragMode.Top;
        if (bottom) return DragMode.Bottom;

        if (y < titleTop || y > titleBottom) return DragMode.None;
        if (In(_layout.Close, x, y) || In(_layout.Pin, x, y)) return DragMode.None;
        return DragMode.Move;
    }

    private void BeginDrag(DragMode mode, int x, int y)
    {
        _drag = mode;
        _dragStartCursor = new ShellNative.POINT(Cursor().X, Cursor().Y);
        _dragStartRect = CurrentRect();
        ShellNative.SetCapture(_hwnd);
        _ = x;
        _ = y;
    }

    private void UpdateDrag(int x, int y)
    {
        if (_drag is DragMode.None or DragMode.Markup or DragMode.Pan) return;
        (int cx, int cy) = Cursor();
        int dx = cx - _dragStartCursor.X;
        int dy = cy - _dragStartCursor.Y;
        int minW = Px(MinWidthDip), minH = Px(MinHeightDip);
        var area = new ShellNative.RECT();
        ShellNative.SystemParametersInfoW(ShellNative.SPI_GETWORKAREA, 0, ref area, 0);

        int left = _dragStartRect.Left, top = _dragStartRect.Top;
        int right = _dragStartRect.Right, bottom = _dragStartRect.Bottom;

        if (_drag is DragMode.Move)
        {
            left += dx;
            top += dy;
            right += dx;
            bottom += dy;
            // 至少留 80 DIP 在屏幕内，避免拖丢窗口。
            int keep = Px(80);
            left = Math.Clamp(left, area.Left - (right - left) + keep, area.Right - keep);
            top = Math.Clamp(top, area.Top, area.Bottom - Px(48));
            right = left + (right - _dragStartRect.Left);
            bottom = top + (bottom - _dragStartRect.Top);
        }
        else
        {
            if (_drag is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft)
                left = Math.Min(left + dx, right - minW);
            if (_drag is DragMode.Right or DragMode.TopRight or DragMode.BottomRight)
                right = Math.Max(right + dx, left + minW);
            if (_drag is DragMode.Top or DragMode.TopLeft or DragMode.TopRight)
                top = Math.Min(top + dy, bottom - minH);
            if (_drag is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight)
                bottom = Math.Max(bottom + dy, top + minH);
        }

        int w = right - left, h = bottom - top;
        ShellNative.SetWindowPos(_hwnd, _pinned ? ShellNative.HwndTopmost : IntPtr.Zero,
            left, top, w, h, ShellNative.SWP_NOACTIVATE);
        _posX = left;
        _posY = top;
        if (_drag != DragMode.Move && _canvas is not null
            && (_canvas.WidthPx != w || _canvas.HeightPx != h))
        {
            long now = Environment.TickCount64;
            if (now - _lastResizeTick >= 33)
            {
                _lastResizeTick = now;
                _canvas.Dispose();
                _canvas = new RopeCanvas(w, h, _scale) { Theme = ThemePalette.Current };
                Render();
            }
        }
    }

    private void EndDrag()
    {
        if (_drag is DragMode.Markup or DragMode.Pan) return;
        if (_drag != DragMode.None)
        {
            ShellNative.ReleaseCapture();
            var rect = CurrentRect();
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            _posX = rect.Left;
            _posY = rect.Top;
            if (_canvas is null || _canvas.WidthPx != w || _canvas.HeightPx != h)
            {
                _canvas?.Dispose();
                _canvas = new RopeCanvas(w, h, _scale) { Theme = ThemePalette.Current };
            }
            Render();
        }
        _drag = DragMode.None;
    }

    private void ToggleMaximize()
    {
        var area = new ShellNative.RECT();
        if (!ShellNative.SystemParametersInfoW(ShellNative.SPI_GETWORKAREA, 0, ref area, 0))
            return;
        if (!_maximized)
        {
            _restoreRect = CurrentRect();
            _maximized = true;
            _posX = area.Left;
            _posY = area.Top;
            ShellNative.SetWindowPos(_hwnd, _pinned ? ShellNative.HwndTopmost : IntPtr.Zero,
                area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top,
                ShellNative.SWP_NOACTIVATE);
        }
        else
        {
            _maximized = false;
            _posX = _restoreRect.Left;
            _posY = _restoreRect.Top;
            ShellNative.SetWindowPos(_hwnd, _pinned ? ShellNative.HwndTopmost : IntPtr.Zero,
                _restoreRect.Left, _restoreRect.Top,
                _restoreRect.Right - _restoreRect.Left, _restoreRect.Bottom - _restoreRect.Top,
                ShellNative.SWP_NOACTIVATE);
        }
        var rect = CurrentRect();
        _canvas?.Dispose();
        _canvas = new RopeCanvas(rect.Right - rect.Left, rect.Bottom - rect.Top, _scale)
        {
            Theme = ThemePalette.Current,
        };
        Render();
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        ApplyZOrder();
        _status = _pinned ? "已置顶（图钉）" : "已取消置顶";
        Render();
    }

    private void ToggleZoom()
    {
        if (Math.Abs(_zoom - 1.0) < 0.001) SetZoom(1.5, 0, 0);
        else SetZoom(1.0, 0, 0);
    }

    /// <summary>客户区坐标 → 画布基准坐标（缩放/平移前的坐标，标注始终按原图存）。</summary>
    private (int X, int Y) ToBasePoint(int x, int y)
    {
        var c = _layout.Canvas;
        if (c.W <= 0 || (Math.Abs(_zoom - 1) < 0.001 && _panX == 0 && _panY == 0)) return (x, y);
        double cx = c.X + c.W / 2.0, cy = c.Y + c.H / 2.0;
        return ((int)Math.Round(cx + (x - cx - _panX) / _zoom),
                (int)Math.Round(cy + (y - cy - _panY) / _zoom));
    }

    // ------------------------------------------------------------------
    // 标注草稿：每笔落盘，崩溃/误关后可恢复（P0-2 的兜底）
    // ------------------------------------------------------------------

    private static string DraftPath => Path.Combine(Path.GetTempPath(), "ClipLine", "markup-draft.json");

    private sealed record DraftShape(int K, int X0, int Y0, int X1, int Y1);

    private sealed record DraftDocument(string Source, List<DraftShape> Shapes);

    private void SaveDraft()
    {
        if (!_dirty || _shapesPath.Length == 0) return;
        try
        {
            string? dir = Path.GetDirectoryName(DraftPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var doc = new DraftDocument(_shapesPath,
                _shapes.ConvertAll(s => new DraftShape(s.Kind, s.X0, s.Y0, s.X1, s.Y1)));
            File.WriteAllText(DraftPath, JsonSerializer.Serialize(doc));
        }
        catch { /* 草稿失败不影响绘制 */ }
    }

    private void RestoreDraft(string source)
    {
        try
        {
            if (!File.Exists(DraftPath)) return;
            DraftDocument? doc = JsonSerializer.Deserialize<DraftDocument>(File.ReadAllText(DraftPath));
            if (doc is null || !string.Equals(doc.Source, source, StringComparison.OrdinalIgnoreCase))
                return;
            foreach (DraftShape s in doc.Shapes)
                _shapes.Add(new MarkShape(s.K, s.X0, s.Y0, s.X1, s.Y1));
            if (_shapes.Count > 0)
            {
                _dirty = true;
                _status = $"已恢复上次未保存的 {_shapes.Count} 条标注 · Ctrl+S 保存";
            }
        }
        catch { /* 坏草稿忽略 */ }
    }

    private void DeleteDraft()
    {
        try { if (File.Exists(DraftPath)) File.Delete(DraftPath); } catch { /* 忽略 */ }
    }

    // ------------------------------------------------------------------
    // 命中与消息
    // ------------------------------------------------------------------

    private ToolHover HitHover(int x, int y, out int index)
    {
        index = -1;
        if (_confirmClose)
        {
            if (In(_layout.ConfirmSave, x, y)) return ToolHover.ConfirmSave;
            if (In(_layout.ConfirmDiscard, x, y)) return ToolHover.ConfirmDiscard;
            if (In(_layout.ConfirmCancel, x, y)) return ToolHover.ConfirmCancel;
            return ToolHover.None;
        }
        if (In(_layout.Close, x, y)) return ToolHover.Close;
        if (In(_layout.Pin, x, y)) return ToolHover.Pin;
        if (In(_layout.Save, x, y)) return ToolHover.Save;
        if (In(_layout.Cancel, x, y) || In(_layout.PutBack, x, y)) return ToolHover.Cancel;
        if (In(_layout.Trim, x, y)) return ToolHover.Primary;
        for (int i = 0; i < _layout.Tools.Count; i++)
            if (In(_layout.Tools[i], x, y)) { index = i; return ToolHover.Tool; }
        for (int i = 0; i < _layout.Thumbs.Count; i++)
            if (In(_layout.Thumbs[i], x, y)) { index = i; return ToolHover.Thumb; }
        return ToolHover.None;
    }

    private void UpdateHover(int x, int y)
    {
        ToolHover hover = HitHover(x, y, out int index);
        if (hover == _hover && index == _hoverIndex) return;
        _hover = hover;
        _hoverIndex = index;
        var tme = new ShellNative.TRACKMOUSEEVENT
        {
            CbSize = (uint)Marshal.SizeOf<ShellNative.TRACKMOUSEEVENT>(),
            DwFlags = ShellNative.TmeLeave,
            HwndTrack = _hwnd,
        };
        ShellNative.TrackMouseEvent(ref tme);
        Render();
    }

    private IntPtr OnLeftDown(int x, int y)
    {
        if (_confirmClose)
        {
            _hover = HitHover(x, y, out _hoverIndex);
            _pressed = true;
            Render();
            return IntPtr.Zero;
        }

        if (_mode == ToolMode.Markup && In(_layout.Canvas, x, y))
        {
            if (_spaceDown)
            {
                BeginDrag(DragMode.Pan, x, y);
                return IntPtr.Zero;
            }
            PushUndo();
            _dragging = true;
            _drag = DragMode.Markup;
            (int mx, int my) = ToBasePoint(x, y);
            _dx0 = _dx1 = mx;
            _dy0 = _dy1 = my;
            _downX = x;
            _downY = y;
            ShellNative.SetCapture(_hwnd);
            Render();
            return IntPtr.Zero;
        }

        DragMode zone = HitDragZone(x, y);
        if (zone != DragMode.None)
        {
            BeginDrag(zone, x, y);
            return IntPtr.Zero;
        }

        _hover = HitHover(x, y, out _hoverIndex);
        _pressed = true;
        Render();
        return IntPtr.Zero;
    }

    private void OnLeftUp(int x, int y)
    {
        if (_confirmClose)
        {
            _pressed = false;
            ToolHover confirmHit = HitHover(x, y, out _);
            if (confirmHit == ToolHover.ConfirmSave) SaveMarkup();
            else if (confirmHit == ToolHover.ConfirmDiscard)
            {
                DeleteDraft();
                CloseNow(true);
            }
            else if (confirmHit == ToolHover.ConfirmCancel)
            {
                _confirmClose = false;
                Render();
            }
            else Render();
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            _drag = DragMode.None;
            ShellNative.ReleaseCapture();
            bool moved = Math.Abs(x - _downX) + Math.Abs(y - _downY) > 3;
            if (moved)
            {
                (int bx, int by) = ToBasePoint(x, y);
                _shapes.Add(new MarkShape(_toolIndex, _dx0, _dy0, bx, by));
                _status = $"已添加 {_shapes.Count} 条标注 · Ctrl+Z 撤销 · Ctrl+S 保存";
                SaveDraft();
            }
            else
            {
                // 单击画布没有产生形状，回滚刚压入的撤销快照。
                if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
            }
            Render();
            return;
        }

        if (_drag is DragMode.Move or DragMode.Left or DragMode.Right or DragMode.Top
            or DragMode.Bottom or DragMode.TopLeft or DragMode.TopRight
            or DragMode.BottomLeft or DragMode.BottomRight)
        {
            EndDrag();
            return;
        }
        if (_drag == DragMode.Pan)
        {
            _drag = DragMode.None;
            ShellNative.ReleaseCapture();
            return;
        }

        _pressed = false;
        ToolHover hit = HitHover(x, y, out int index);
        switch (hit)
        {
            case ToolHover.Close:
                RequestClose(true);
                break;
            case ToolHover.Pin:
                TogglePin();
                break;
            case ToolHover.Save:
                SaveMarkup();
                break;
            case ToolHover.Cancel:
                RequestClose(true);
                break;
            case ToolHover.Primary:
                OpenPrimary();
                break;
            case ToolHover.Tool:
                _toolIndex = index;
                Render();
                break;
            case ToolHover.Thumb:
                if (index >= 0 && index < _layout.ThumbIndexes.Count)
                {
                    _index = Math.Clamp(_layout.ThumbIndexes[index], 0, Math.Max(0, _paths.Count - 1));
                    _subtitle = Path.GetFileName(_paths[_index]);
                    _zoom = 1.0;
                    _panX = _panY = 0;
                }
                Render();
                break;
            default:
                Render();
                break;
        }
    }

    /// <summary>修剪窗主按钮：交给系统播放器，不再假装编码（P0-4）。</summary>
    private void OpenPrimary()
    {
        if (_paths.Count == 0) return;
        string path = _paths[_index];
        if (!File.Exists(path)) { _status = "文件已不在原位置"; Render(); return; }
        ShellNative.ShellExecuteW(_hwnd, "open", path, null, null, ShellNative.SwShowNormal);
        _status = "已用系统播放器打开";
        Render();
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
            case ShellNative.WM_MOUSELEAVE:
                if (!window._dragging && window._drag == DragMode.None)
                {
                    window._hover = ToolHover.None;
                    window._hoverIndex = -1;
                    window._pressed = false;
                    window.Render();
                }
                return IntPtr.Zero;

            case ShellNative.WM_MOUSEMOVE:
            {
                (int x, int y) = Point(lParam);
                if (window._drag == DragMode.Pan)
                {
                    var cursor = Cursor();
                    window._panX += cursor.X - window._dragStartCursor.X;
                    window._panY += cursor.Y - window._dragStartCursor.Y;
                    window._dragStartCursor = new ShellNative.POINT(cursor.X, cursor.Y);
                    window.ClampPan();
                    window.Render();
                    return IntPtr.Zero;
                }
                if (window._dragging)
                {
                    if ((wParam.ToUInt64() & ShellNative.MkLButton) == 0) return IntPtr.Zero;
                    if (x == window._dx1 && y == window._dy1) return IntPtr.Zero;
                    (int bx, int by) = window.ToBasePoint(x, y);
                    window._dx1 = bx;
                    window._dy1 = by;
                    window.Render();
                    return IntPtr.Zero;
                }
                if (window._drag != DragMode.None)
                {
                    window.UpdateDrag(x, y);
                    return IntPtr.Zero;
                }
                window.UpdateHover(x, y);
                return IntPtr.Zero;
            }

            case ShellNative.WM_LBUTTONDOWN:
                return window.OnLeftDown(Point(lParam).X, Point(lParam).Y);

            case ShellNative.WM_LBUTTONUP:
                window.OnLeftUp(Point(lParam).X, Point(lParam).Y);
                return IntPtr.Zero;

            case ShellNative.WM_LBUTTONDBLCLK:
            {
                (int x, int y) = Point(lParam);
                if (window.HitDragZone(x, y) == DragMode.Move)
                {
                    ShellNative.ReleaseCapture();
                    window._drag = DragMode.None;
                    window.ToggleMaximize();
                }
                return IntPtr.Zero;
            }

            case ShellNative.WM_MOUSEWHEEL:
            {
                int delta = (short)((wParam.ToUInt64() >> 16) & 0xFFFF);
                if ((ShellNative.GetKeyState(ShellNative.VkControl) & 0x8000) != 0)
                {
                    (int x, int y) = Point(lParam);
                    window.SetZoom(window._zoom * (delta > 0 ? 1.15 : 1 / 1.15), x, y);
                    return IntPtr.Zero;
                }
                if (window._mode == ToolMode.QuickLook)
                {
                    window.Step(delta > 0 ? -1 : 1);
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;
            }

            case ShellNative.WM_KEYUP:
            {
                uint vk = (uint)wParam.ToUInt64();
                if (vk == ShellNative.VkSpace) window._spaceDown = false;
                return IntPtr.Zero;
            }

            case ShellNative.WM_KEYDOWN:
                return window.OnKeyDown((uint)wParam.ToUInt64());

            case ShellNative.WM_DPICHANGED:
            {
                uint dpi = (uint)(wParam.ToUInt64() >> 16);
                if (dpi == 0) dpi = ShellNative.GetDpiForWindow(hWnd);
                window._scale = dpi / 96.0;
                var suggested = Marshal.PtrToStructure<ShellNative.RECT>(lParam);
                int w = suggested.Right - suggested.Left;
                int h = suggested.Bottom - suggested.Top;
                window._posX = suggested.Left;
                window._posY = suggested.Top;
                ShellNative.SetWindowPos(hWnd, window._pinned ? ShellNative.HwndTopmost : IntPtr.Zero,
                    suggested.Left, suggested.Top, w, h, ShellNative.SWP_NOACTIVATE);
                window._canvas?.Dispose();
                window._canvas = new RopeCanvas(w, h, window._scale)
                {
                    Theme = ThemePalette.Current,
                };
                window.Render();
                return IntPtr.Zero;
            }

            case ShellNative.WM_CLOSE:
                window.RequestClose(true);
                return IntPtr.Zero;

            case ShellNative.WM_DESTROY:
                Windows.Remove(hWnd);
                return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnKeyDown(uint vk)
    {
        bool ctrl = (ShellNative.GetKeyState(ShellNative.VkControl) & 0x8000) != 0;
        bool shift = (ShellNative.GetKeyState(ShellNative.VkShift) & 0x8000) != 0;

        if (_confirmClose)
        {
            if (vk == ShellNative.VkEscape) { _confirmClose = false; Render(); }
            else if (vk == ShellNative.VkEnter) SaveMarkup();
            return IntPtr.Zero;
        }

        if (vk == ShellNative.VkEscape)
        {
            RequestClose(true);
            return IntPtr.Zero;
        }
        if (vk == ShellNative.VkSpace)
        {
            // 只有快速查看/拿在手上用空格关闭；标注里空格是抓手，绝不丢内容。
            if (_mode == ToolMode.QuickLook || _mode == ToolMode.Held) RequestClose(true);
            else { _spaceDown = true; _status = "抓手模式：拖拽平移画布"; Render(); }
            return IntPtr.Zero;
        }
        if (vk == ShellNative.VkLeft) { if (_mode == ToolMode.QuickLook) Step(-1); return IntPtr.Zero; }
        if (vk == ShellNative.VkRight) { if (_mode == ToolMode.QuickLook) Step(1); return IntPtr.Zero; }
        if (vk == ShellNative.VkTab)
        {
            if (_mode == ToolMode.QuickLook) Step(shift ? -1 : 1);
            return IntPtr.Zero;
        }
        if (ctrl && vk == ShellNative.VkS)
        {
            if (_mode == ToolMode.Markup) SaveMarkup();
            return IntPtr.Zero;
        }
        if (ctrl && vk == ShellNative.VkZ)
        {
            if (_mode == ToolMode.Markup) { if (shift) Redo(); else Undo(); }
            return IntPtr.Zero;
        }
        if (ctrl && vk == ShellNative.VkY)
        {
            if (_mode == ToolMode.Markup) Redo();
            return IntPtr.Zero;
        }
        if (ctrl && (vk == 0x30 || vk == 0xBB || vk == 0xBD))
        {
            if (vk == 0x30) SetZoom(1.0, 0, 0);
            else SetZoom(_zoom * (vk == 0xBB ? 1.15 : 1 / 1.15), 0, 0);
            return IntPtr.Zero;
        }
        if (vk == ShellNative.VkDelete && _mode == ToolMode.Markup && _shapes.Count > 0)
        {
            PushUndo();
            _shapes.RemoveAt(_shapes.Count - 1);
            _status = "已删除最后一条标注";
            SaveDraft();
            Render();
            return IntPtr.Zero;
        }
        if (ctrl && vk == ShellNative.VkC && _mode == ToolMode.Held && _itemId != Guid.Empty)
        {
            _host.Facade.Handle(new HeldWindowCommand(HeldAction.Copy, _itemId));
            _status = "已复制（Ctrl+C）";
            Render();
            return IntPtr.Zero;
        }
        return ShellNative.DefWindowProcW(_hwnd, ShellNative.WM_KEYDOWN, (UIntPtr)vk, IntPtr.Zero);
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
