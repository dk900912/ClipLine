using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipLine.Shell;

/// <summary>
/// 标注用的文字输入窗（P1-10）。
///
/// 刻意做成「普通窗口 + 原生 EDIT」而不是在分层自绘画布上伪造输入框：
/// 中文输入法的候选窗、选区、右键菜单、Ctrl+A/C/V、触摸选择全由系统提供，
/// 自绘做不到这些还不被用户发现。窗口本体（标题、输入框描边、两个按钮）
/// 仍是自绘，保证深浅色与主界面一致。
/// </summary>
internal sealed class TextInputWindow : IDisposable
{
    private const string ClassName = "ClipLine_TextInput";
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsTabStop = 0x00010000;
    private const uint WsVScroll = 0x00200000;
    private const uint ExDlgModalFrame = 0x00000001;
    private const uint EsMultiLine = 0x0004;
    private const uint EsAutoVScroll = 0x0040;
    private const uint EsWantReturn = 0x1000;

    private const int WmSize = 0x0005;
    private const int WmClose = 0x0010;
    private const int WmCtlColorEdit = 0x0133;
    private const int WmSetFont = 0x0030;
    private const int WmGetDlgCode = 0x0087;
    private const int GwlWndProc = -4;
    private const uint DlGcWantAllKeys = 0x0004;

    private static readonly Dictionary<IntPtr, TextInputWindow> Windows = new();
    private static readonly Dictionary<IntPtr, TextInputWindow> Edits = new();
    private static ShellNative.WndProcDelegate? s_proc;
    private static WndProcDelegate? s_editProc;
    private static bool s_classRegistered;

    private readonly double _scale;
    private IntPtr _hwnd;
    private IntPtr _edit;
    private IntPtr _oldEditProc;
    private IntPtr _font;
    private IntPtr _editBrush;
    private int _width;
    private int _height;
    private string _title = "添加文字";
    private Action<string>? _onAccept;
    private bool _accepted;
    private (int X, int Y, int W, int H) _ok;
    private (int X, int Y, int W, int H) _cancel;

    private TextInputWindow(double scale) => _scale = scale <= 0 ? 1.0 : scale;

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    /// <summary>打开输入窗；<paramref name="onAccept"/> 只在「确定」且非空时回调。</summary>
    internal static void Show(IntPtr owner, double scale, string title, string initial,
        Action<string> onAccept)
    {
        var window = new TextInputWindow(scale)
        {
            _title = title,
            _onAccept = onAccept,
        };
        window.Create(owner, initial);
    }

    private void Create(IntPtr owner, string initial)
    {
        EnsureClass();
        _width = Px(480);
        _height = Px(320);

        int x = 200, y = 200;
        if (owner != IntPtr.Zero && ShellNative.GetWindowRect(owner, out ShellNative.RECT o))
        {
            x = o.Left + (o.Right - o.Left - _width) / 2;
            y = o.Top + (o.Bottom - o.Top - _height) / 2;
        }

        _hwnd = ShellNative.CreateWindowExW(ExDlgModalFrame, ClassName, _title,
            WsPopup | WsCaption | WsSysMenu, x, y, _width, _height, owner, IntPtr.Zero,
            ShellNative.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;
        Windows[_hwnd] = this;
        WindowChrome.Apply(_hwnd);

        ThemePalette theme = ThemePalette.Current;
        _editBrush = CreateSolidBrush(Rgb(theme.Surface));
        _font = ShellNative.CreateFontW(-Px(14), 0, 0, 0, 400, 0, 0, 0,
            1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0,
            "Microsoft YaHei UI");

        int pad = Px(16);
        LayoutClient();
        _edit = ShellNative.CreateWindowExW(0, "EDIT", initial,
            WsChild | WsVisible | WsTabStop | WsVScroll | EsMultiLine | EsAutoVScroll | EsWantReturn,
            pad, Px(16), _clientW - pad * 2, EditHeight(), _hwnd, new IntPtr(1001),
            ShellNative.GetModuleHandleW(null), IntPtr.Zero);
        if (_edit != IntPtr.Zero)
        {
            Edits[_edit] = this;
            ShellNative.SendMessageW(_edit, WmSetFont, ToW(_font), new IntPtr(1));
            s_editProc ??= EditProc;
            _oldEditProc = ShellNative.SetWindowLongPtrW(_edit, GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(s_editProc));
        }

        LayoutButtons();
        ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOW);
        ShellNative.SetForegroundWindow(_hwnd);
        if (_edit != IntPtr.Zero)
        {
            ShellNative.SetFocus(_edit);
            // 打开时全选：改字比重新输入更常见。
            ShellNative.SendMessageW(_edit, 0x00B1 /* EM_SETSEL */, UIntPtr.Zero,
                new IntPtr(initial.Length));
        }
    }

    private int _clientW;
    private int _clientH;

    /// <summary>客户区尺寸才是不含标题栏的可用空间；窗口高度会随 DPI 与标题栏变化。</summary>
    private void LayoutClient()
    {
        if (_hwnd != IntPtr.Zero && GetClientRect(_hwnd, out ShellNative.RECT rc)
            && rc.Right > 0 && rc.Bottom > 0)
        {
            _clientW = rc.Right;
            _clientH = rc.Bottom;
        }
        else
        {
            _clientW = _width;
            _clientH = _height - Px(32);
        }
    }

    private int EditHeight() => Math.Max(Px(80), _clientH - Px(16) - Px(56));

    private void LayoutButtons()
    {
        int bw = Px(88), bh = Px(32), gap = Px(8);
        int by = _clientH - bh - Px(14);
        _cancel = (_clientW - Px(16) - bw * 2 - gap, by, bw, bh);
        _ok = (_clientW - Px(16) - bw, by, bw, bh);
    }

    private void Finish(bool accept)
    {
        if (_accepted) return;
        _accepted = true;
        string text = string.Empty;
        if (_edit != IntPtr.Zero)
        {
            int len = GetWindowTextLength(_edit);
            if (len > 0)
            {
                var sb = new StringBuilder(len + 2);
                GetWindowText(_edit, sb, sb.Capacity);
                text = sb.ToString().Trim();
            }
        }
        if (accept && text.Length > 0) _onAccept?.Invoke(text);
        if (_hwnd != IntPtr.Zero) ShellNative.DestroyWindow(_hwnd);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (!Windows.TryGetValue(hWnd, out TextInputWindow? window))
            return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WmSize:
                window._width = (int)(lParam.ToInt64() & 0xFFFF);
                window._height = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                window.LayoutClient();
                if (window._edit != IntPtr.Zero)
                {
                    int pad = window.Px(16);
                    MoveWindow(window._edit, pad, window.Px(16), window._clientW - pad * 2,
                        window.EditHeight(), true);
                }
                window.LayoutButtons();
                InvalidateRect(hWnd, IntPtr.Zero, true);
                return IntPtr.Zero;

            case WmCtlColorEdit:
                IntPtr editDc = ToIntPtr(wParam);
                ShellNative.SetBkMode(editDc, 1 /* TRANSPARENT */);
                ShellNative.SetTextColor(editDc, (uint)Rgb(ThemePalette.Current.TextPrimary));
                return window._editBrush;

            case 0x000F: // WM_PAINT
                window.Paint();
                return IntPtr.Zero;

            case 0x0201: // WM_LBUTTONDOWN
                return IntPtr.Zero;

            case 0x0202: // WM_LBUTTONUP
            {
                (int x, int y) = Point(lParam);
                if (In(window._ok, x, y)) { window.Finish(true); return IntPtr.Zero; }
                if (In(window._cancel, x, y)) { window.Finish(false); return IntPtr.Zero; }
                return IntPtr.Zero;
            }

            case ShellNative.WM_KEYDOWN:
            {
                uint vk = (uint)wParam.ToUInt64();
                if (vk == ShellNative.VkEscape) { window.Finish(false); return IntPtr.Zero; }
                if (vk == ShellNative.VkEnter) { window.Finish(true); return IntPtr.Zero; }
                return IntPtr.Zero;
            }

            case WmClose:
                window.Finish(false);
                return IntPtr.Zero;

            case 0x0002: // WM_DESTROY
                window.Dispose();
                return IntPtr.Zero;
        }
        return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>EDIT 子类化：只截两件事 —— Esc 取消、Ctrl+Enter 提交；其余全交回系统。</summary>
    private static IntPtr EditProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (!Edits.TryGetValue(hWnd, out TextInputWindow? window))
            return ShellNative.DefWindowProcW(hWnd, msg, wParam, lParam);

        if (msg == WmGetDlgCode) return new IntPtr(DlGcWantAllKeys);

        if (msg == ShellNative.WM_KEYDOWN)
        {
            uint vk = (uint)wParam.ToUInt64();
            bool ctrl = (ShellNative.GetKeyState(ShellNative.VkControl) & 0x8000) != 0;
            if (vk == ShellNative.VkEscape) { window.Finish(false); return IntPtr.Zero; }
            if (vk == ShellNative.VkEnter && ctrl) { window.Finish(true); return IntPtr.Zero; }
        }
        return CallWindowProc(window._oldEditProc, hWnd, msg, wParam, lParam);
    }

    private void Paint()
    {
        IntPtr dc = BeginPaint(_hwnd, out PAINTSTRUCT ps);
        try
        {
            ThemePalette t = ThemePalette.Current;
            FillRect(dc, ref ps.RcPaint, ThemeBrushes.Window(t));

            // 输入框描边：EDIT 自己填 Surface，这里只画 1 DIP 外框 + 4 DIP 圆角。
            int pad = Px(16);
            StrokeRoundRect(dc, pad - Px(1), Px(15), _clientW - pad * 2 + Px(2),
                EditHeight() + Px(2), Px(4), t.ControlBorder);

            DrawText(dc, "支持多行；Ctrl+Enter 确定，Esc 取消", pad,
                _clientH - Px(34), Px(12), 400, t.TextMuted);
            DrawButton(dc, _cancel, "取消", false, t);
            DrawButton(dc, _ok, "确定", true, t);
        }
        finally
        {
            EndPaint(_hwnd, ref ps);
        }
    }

    private void DrawButton(IntPtr dc, (int X, int Y, int W, int H) r, string label,
        bool primary, ThemePalette t)
    {
        FillRoundRect(dc, r.X, r.Y, r.W, r.H, Px(4),
            primary ? t.Accent : t.Control, primary ? t.Accent : t.ControlBorder);
        int fontH = Px(13);
        int textW = TextWidth(dc, label, fontH, primary ? 600 : 400);
        DrawText(dc, label, r.X + (r.W - textW) / 2, r.Y + (r.H - fontH) / 2,
            fontH, primary ? 600 : 400, primary ? t.OnAccent : t.TextPrimary);
    }

    private static string FaceFor(string text)
    {
        foreach (char ch in text)
            if (ch > 0x2E80) return "Microsoft YaHei UI";
        return "Segoe UI";
    }

    private static void DrawText(IntPtr dc, string text, int x, int y, int fontH, int weight, int rgb)
    {
        IntPtr font = ShellNative.CreateFontW(-fontH, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0,
            FaceFor(text));
        IntPtr old = SelectObject(dc, font);
        ShellNative.SetBkMode(dc, 1 /* TRANSPARENT */);
        ShellNative.SetTextColor(dc, (uint)Rgb(rgb));
        ShellNative.TextOutW(dc, x, y, text, text.Length);
        SelectObject(dc, old);
        ShellNative.DeleteObject(font);
    }

    private static int TextWidth(IntPtr dc, string text, int fontH, int weight)
    {
        IntPtr font = ShellNative.CreateFontW(-fontH, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0,
            FaceFor(text));
        IntPtr old = SelectObject(dc, font);
        int width = 0;
        if (ShellNative.GetTextExtentPoint32W(dc, text, text.Length, out ShellNative.SIZE size))
            width = size.CX;
        SelectObject(dc, old);
        ShellNative.DeleteObject(font);
        return width;
    }

    private static void FillRoundRect(IntPtr dc, int x, int y, int w, int h, int radius,
        int fill, int border)
    {
        IntPtr brush = CreateSolidBrush(Rgb(fill));
        IntPtr pen = CreatePen(0, 1, Rgb(border));
        IntPtr oldBrush = SelectObject(dc, brush);
        IntPtr oldPen = SelectObject(dc, pen);
        RoundRect(dc, x, y, x + w, y + h, radius, radius);
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        ShellNative.DeleteObject(brush);
        ShellNative.DeleteObject(pen);
    }

    private static void StrokeRoundRect(IntPtr dc, int x, int y, int w, int h, int radius, int rgb)
    {
        IntPtr pen = CreatePen(0, 1, Rgb(rgb));
        IntPtr oldPen = SelectObject(dc, pen);
        IntPtr nullBrush = GetStockObject(5 /* NULL_BRUSH */);
        IntPtr oldBrush = SelectObject(dc, nullBrush);
        RoundRect(dc, x, y, x + w, y + h, radius, radius);
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        ShellNative.DeleteObject(pen);
    }

    private static (int X, int Y) Point(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        return ((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    private static bool In((int X, int Y, int W, int H) r, int x, int y) =>
        x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    private static UIntPtr ToW(IntPtr value) => (UIntPtr)(ulong)value.ToInt64();

    private static IntPtr ToIntPtr(UIntPtr value) => new((long)value.ToUInt64());

    /// <summary>#RRGGBB → GDI 的 COLORREF（0x00BBGGRR）。</summary>
    private static int Rgb(int rgb) => (rgb & 0xFF) << 16 | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static void EnsureClass()
    {
        if (s_classRegistered) return;
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
            throw new InvalidOperationException("注册文字输入窗类失败，Win32Error=" + Marshal.GetLastWin32Error());
        s_classRegistered = true;
    }

    public void Dispose()
    {
        if (_edit != IntPtr.Zero)
        {
            Edits.Remove(_edit);
            if (_oldEditProc != IntPtr.Zero)
                ShellNative.SetWindowLongPtrW(_edit, GwlWndProc, _oldEditProc);
            _edit = IntPtr.Zero;
        }
        IntPtr hwnd = _hwnd;
        _hwnd = IntPtr.Zero; // 先清空：WM_DESTROY 会回调到这里，避免递归销毁
        if (hwnd != IntPtr.Zero)
        {
            Windows.Remove(hwnd);
            if (ShellNative.IsWindow(hwnd)) ShellNative.DestroyWindow(hwnd);
        }
        if (_editBrush != IntPtr.Zero) { ShellNative.DeleteObject(_editBrush); _editBrush = IntPtr.Zero; }
        if (_font != IntPtr.Zero) { ShellNative.DeleteObject(_font); _font = IntPtr.Zero; }
    }

    // ---- 本窗专用的 GDI 合约（ShellNative 只保留窗口/自绘共用的那部分） ----

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        public int FErase;
        public ShellNative.RECT RcPaint;
        public int FRestore;
        public int FIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] RgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg,
        UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "BeginPaint")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll", EntryPoint = "EndPaint")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll", EntryPoint = "FillRect")]
    private static extern int FillRect(IntPtr hdc, ref ShellNative.RECT rect, IntPtr brush);

    [DllImport("user32.dll", EntryPoint = "GetClientRect")]
    private static extern bool GetClientRect(IntPtr hWnd, out ShellNative.RECT rect);

    [DllImport("user32.dll", EntryPoint = "InvalidateRect")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll", EntryPoint = "MoveWindow")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll", EntryPoint = "CreatePen")]
    private static extern IntPtr CreatePen(int style, int width, int color);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", EntryPoint = "RoundRect")]
    private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom,
        int width, int height);

    [DllImport("gdi32.dll", EntryPoint = "GetStockObject")]
    private static extern IntPtr GetStockObject(int index);

    /// <summary>窗口背景刷按色值缓存：WM_PAINT 每帧都来，不能每次新建。</summary>
    private static class ThemeBrushes
    {
        private static readonly Dictionary<int, IntPtr> Cache = new();

        internal static IntPtr Window(ThemePalette t)
        {
            int rgb = t.Window;
            if (Cache.TryGetValue(rgb, out IntPtr brush)) return brush;
            brush = CreateSolidBrush(Rgb(rgb));
            Cache[rgb] = brush;
            return brush;
        }
    }
}
