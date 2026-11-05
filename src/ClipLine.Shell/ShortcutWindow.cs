using System;
using System.Runtime.InteropServices;
using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>
/// 快捷键录制器（P1-5）。取代原来「点菜单只弹一句 v1 固定为 Ctrl+Alt+T」的假入口：
/// 这里按下什么就显示什么，注册失败立刻回滚并说明原因，另有「恢复默认」。
/// 与更新面板同一套自绘分层窗口 + 可拖动标题栏 + 主题联动。
/// </summary>
internal sealed class ShortcutWindow : IDisposable
{
    public const string ClassName = "ClipLine_ShortcutWindow";
    private const int WidthDip = 440;
    private const int HeightDip = 250;

    private static readonly Dictionary<IntPtr, ShortcutWindow> Windows = new();
    private static ShellNative.WndProcDelegate? s_proc;
    private static bool s_classRegistered;

    private readonly double _scale;
    private readonly AppFacade _facade;
    private readonly ShortcutLayout _layout = new();
    private IntPtr _hwnd;
    private RopeCanvas? _canvas;
    private ShortcutHover _hover;
    private bool _pressed;
    private bool _dragging;
    private bool _disposed;
    private int _posX, _posY;
    private ShellNative.POINT _dragStart;
    private ShellNative.RECT _dragRect;
    private string? _candidate;
    private string _message = "按一次组合键即可生效；Delete 清空，Esc 取消。";

    public ShortcutWindow(double scale, AppFacade facade)
    {
        _scale = scale <= 0 ? 1.0 : scale;
        _facade = facade;
    }

    public IntPtr Handle => _hwnd;
    public bool Exists => _hwnd != IntPtr.Zero && ShellNative.IsWindow(_hwnd);
    public bool IsVisible => Exists && ShellNative.IsWindowVisible(_hwnd);
    internal ShortcutLayout Layout => _layout;

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
            throw new InvalidOperationException("注册快捷键窗口类失败，Win32Error=" +
                Marshal.GetLastWin32Error());
        s_classRegistered = true;
    }

    public bool Show()
    {
        EnsureClass();
        int w = Px(WidthDip), h = Px(HeightDip);
        _candidate = null;
        _message = "按一次组合键即可生效；Delete 清空，Esc 取消。";
        if (!Exists)
        {
            uint ex = ShellNative.WS_EX_LAYERED | ShellNative.WS_EX_APPWINDOW;
            _hwnd = ShellNative.CreateWindowExW(ex, ClassName, "ClipLine — 更改快捷键",
                ShellNative.WS_POPUP, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero,
                ShellNative.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            Windows[_hwnd] = this;
            WindowChrome.Apply(_hwnd); // P2-6 圆角 + 深色边框
            Center(w, h);
        }
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
        _posX = area.Left + (area.Right - area.Left - w) / 2;
        _posY = area.Top + (area.Bottom - area.Top - h) / 2 - Px(20);
        ShellNative.SetWindowPos(_hwnd, IntPtr.Zero, _posX, _posY, w, h, ShellNative.SWP_NOACTIVATE);
    }

    private void Render()
    {
        if (_canvas is null)
            _canvas = new RopeCanvas(Px(WidthDip), Px(HeightDip), _scale) { Theme = ThemePalette.Current };
        _canvas.Theme = ThemePalette.Current;
        _canvas.RenderShortcut(View(), _layout);
        _canvas.Present(_hwnd, 255, _posX, _posY);
    }

    private ShortcutView View() => new(
        _facade.Current.Shortcut,
        _candidate,
        _message,
        _hover,
        _pressed);

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

    private ShortcutHover Hit(int x, int y)
    {
        if (In(_layout.Close, x, y)) return ShortcutHover.Close;
        if (In(_layout.Primary, x, y)) return ShortcutHover.Primary;
        if (In(_layout.Secondary, x, y)) return ShortcutHover.Secondary;
        return ShortcutHover.None;
    }

    /// <summary>把一次按键变成「Ctrl+Alt+T」这样的组合串；不可用的键返回 null 并给出原因。</summary>
    private (string? Combo, string? KeyName, string? Error) Describe(uint vk)
    {
        string? name = vk switch
        {
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            _ => null,
        };
        if (name is null)
            return (null, null, "只支持字母或数字键，换一个试试。");

        var parts = new List<string>(4);
        if (IsDown(ShellNative.VkControl)) parts.Add("Ctrl");
        if (IsDown(ShellNative.VkMenu)) parts.Add("Alt");
        if (IsDown(ShellNative.VkShift)) parts.Add("Shift");
        if (IsDown(ShellNative.VkLWin)) parts.Add("Win");
        if (parts.Count == 0)
            return (null, name, "需要至少一个修饰键：Ctrl / Alt / Shift / Win。");
        parts.Add(name);
        return (string.Join('+', parts), name, null);
    }

    private static bool IsDown(byte vk) => (ShellNative.GetKeyState(vk) & 0x8000) != 0;

    /// <summary>提交一次录制结果，并按门面回写后的实际值给出反馈（成功 / 被占用回滚）。</summary>
    private void Submit(string combo, string keyName, string what)
    {
        string before = _facade.Current.Shortcut;
        _facade.Handle(new ShortcutRecorded(combo, keyName));
        string after = _facade.Current.Shortcut;
        _candidate = null;
        if (after == combo)
        {
            _message = combo == HotKeyModel.NoneShortcut
                ? "已清空快捷键：可用托盘菜单唤出晾衣绳。"
                : $"已设为 {after}，立即生效。";
            return;
        }
        if (after == before)
        {
            // 门面校验没过（缺修饰键）或注册失败后回滚，两种情况都说明原因。
            _message = $"{what} 不可用：该组合已被其他应用占用，仍为 {after}。";
            return;
        }
        _message = $"快捷键现在是 {after}。";
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
        if (!Windows.TryGetValue(hWnd, out ShortcutWindow? window))
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
                ShortcutHover hover = window.Hit(x, y);
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
                    window._hover = ShortcutHover.None;
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
                ShortcutHover hit = window.Hit(x, y);
                if (hit == ShortcutHover.Close || hit == ShortcutHover.Primary) window.Hide();
                else if (hit == ShortcutHover.Secondary)
                    window.Submit(HotKeyModel.DefaultShortcut, "T", HotKeyModel.DefaultShortcut);
                window.Render();
                return IntPtr.Zero;

            case ShellNative.WM_KEYDOWN:
            {
                uint vk = (uint)wParam.ToUInt64();
                if (vk == ShellNative.VkEscape) { window.Hide(); return IntPtr.Zero; }
                // 单独的修饰键不构成组合，等真正的主键。
                if (vk is ShellNative.VkShift or ShellNative.VkControl or ShellNative.VkMenu
                    or ShellNative.VkLWin) return IntPtr.Zero;
                if (vk == ShellNative.VkDelete)
                {
                    window.Submit(HotKeyModel.NoneShortcut, "Delete", "清空");
                    window.Render();
                    return IntPtr.Zero;
                }
                (string? combo, string? keyName, string? error) = window.Describe(vk);
                if (combo is null)
                {
                    window._candidate = keyName;
                    window._message = error ?? "换个键试试。";
                    window.Render();
                    return IntPtr.Zero;
                }
                window.Submit(combo, keyName!, combo);
                window.Render();
                return IntPtr.Zero;
            }

            case ShellNative.WM_CLOSE:
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
