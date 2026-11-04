using System.Runtime.InteropServices;
using System.Text;

namespace ClipLine.P0Spike;

/// <summary>
/// P0 一次性平台验证切片（throwaway）。
///
/// 要回答的问题：Windows 11 上能否用自管分层 HWND 做到
/// 「全屏宽透明 / 默认整窗点击穿透 / 仅卡片区可点 / 不抢焦点 / 收起后不留窗口」，
/// 以及顶部热区 0.25 s 展开、0.5 s 收起、0.3 s 销毁的时序是否可用假时钟确定性验证。
///
/// 运行：
///   ClipLine.P0Spike.exe --self-test   # 假时钟自检，不动真实鼠标，自动退出
///   ClipLine.P0Spike.exe               # 交互模式，真实 30 fps 指针追踪，右键托盘退出
/// </summary>
internal sealed class P0Spike
{
    private const string LineClass = "ClipLine_P0Spike_Line";
    private const string MsgClass = "ClipLine_P0Spike_Msg";

    private const int HotBand = 12;        // 顶边热条高度（DIP）
    private const int PanelHeight = 210;   // 中档：photoMaxHeight 104 + 106
    private const int RevealDelay = 250;
    private const int RetractDelay = 500;
    private const int DestroyDelay = 300;
    private const int FrameMs = 33;        // ≈ 30 fps

    private readonly bool _selfTest;
    private readonly List<(string Name, bool Pass, string Detail)> _checks = new();

    private Native.WndProcDelegate _wndProc = null!;
    private RopeCanvas _canvas = null!;
    private IntPtr _lineHwnd;
    private IntPtr _msgHwnd;
    private IntPtr _trayIcon;
    private int _screenWidth;

    // 状态机（与综合规格 §实现决策 的状态机契约一致）
    private bool _revealed;
    private bool _transparent = true;
    private long _hotZoneSince = -1;
    private long _awaySince = -1;
    private long _collapsedAt = -1;
    private long _now;

    private static int Main(string[] args)
    {
        var spike = new P0Spike(args.Contains("--self-test", StringComparer.OrdinalIgnoreCase));
        return spike.Run();
    }

    private P0Spike(bool selfTest) => _selfTest = selfTest;

    private void Check(string name, bool pass, string detail = "") => _checks.Add((name, pass, detail));

    private int Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== ClipLine P0 平台验证切片（throwaway，不是产品代码）===");
        Console.WriteLine(_selfTest ? "模式：--self-test（假时钟，不移动真实鼠标）" : "模式：交互（真实 30 fps 追踪，右键托盘图标退出）");
        Console.WriteLine();

        try
        {
            Setup();
            if (_selfTest) RunSelfTest();
            else RunInteractive();
        }
        catch (Exception ex)
        {
            Check("切片运行无异常", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Cleanup();
        }

        if (_selfTest)
        {
            foreach (var c in _checks)
                Console.WriteLine($"  [{(c.Pass ? "PASS" : "FAIL")}] {c.Name}{(c.Detail.Length > 0 ? " — " + c.Detail : "")}");
            int passed = _checks.Count(c => c.Pass);
            Console.WriteLine();
            Console.WriteLine($"VERDICT: {(passed == _checks.Count ? "PASS" : "FAIL")} ({passed}/{_checks.Count})");
            return passed == _checks.Count ? 0 : 1;
        }
        return 0;
    }

    private void Setup()
    {
        bool dpiSet = Native.SetProcessDpiAwarenessContext(Native.DpiPerMonitorV2);
        int awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext());
        // GetDpiForWindow 需要有效 HWND（传 NULL 会返回 0），此处用 GetDpiForSystem。
        uint dpi = Native.GetDpiForSystem();
        Check("进程级 DPI 感知 (PMv2)", dpiSet && awareness == 2 && dpi > 0,
            $"SetProcessDpiAwarenessContext={dpiSet}, awareness={awareness}, systemDpi={dpi}");

        _screenWidth = Native.GetSystemMetrics(0);
        if (_screenWidth <= 0) _screenWidth = 1280;
        _canvas = new RopeCanvas(_screenWidth, PanelHeight);
        Check("32bpp 预乘 alpha DIB 画布", true,
            $"{_canvas.Width}×{_canvas.Height}, sag={_canvas.Sag:F1}, 卡片 {RopeCanvas.CardWidth}×{RopeCanvas.CardHeight}");

        IntPtr instance = Native.GetModuleHandleW(null);
        _wndProc = WndProc; // 必须保活，否则回调被 GC 回收
        IntPtr proc = Marshal.GetFunctionPointerForDelegate(_wndProc);
        ushort lineAtom = RegisterClass(LineClass, instance, proc);
        ushort msgAtom = RegisterClass(MsgClass, instance, proc);
        Check("注册窗口类", lineAtom != 0 && msgAtom != 0, $"line={lineAtom}, msg={msgAtom}");

        // 托盘宿主用 message-only 窗口：它不是可见顶层窗口，收起线窗口时不会一起消失
        _msgHwnd = Native.CreateWindowExW(0, MsgClass, "ClipLine P0 host", Native.WS_POPUP,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
        Check("托盘宿主 = message-only 窗口", _msgHwnd != IntPtr.Zero, $"hwnd=0x{_msgHwnd.ToInt64():X}");

        _trayIcon = TrayIcon.CreateDoubleLineIcon();
        bool trayAdded = _msgHwnd != IntPtr.Zero && _trayIcon != IntPtr.Zero
            && TrayIcon.Add(_msgHwnd, _trayIcon, "ClipLine · 晾衣绳上有 1 张");
        Check("托盘图标 (E 双线 16px + Shell_NotifyIconW)", trayAdded, $"hIcon=0x{_trayIcon.ToInt64():X}");

        EnsureLineWindow();
    }

    private static ushort RegisterClass(string name, IntPtr instance, IntPtr proc)
    {
        var wc = new Native.WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            Style = 0,
            LpfnWndProc = proc,
            CbClsExtra = 0,
            CbWndExtra = 0,
            HInstance = instance,
            HIcon = IntPtr.Zero,
            HCursor = IntPtr.Zero,
            HbrBackground = IntPtr.Zero,
            LpszMenuName = null,
            LpszClassName = name,
            HIconSm = IntPtr.Zero,
        };
        return Native.RegisterClassExW(ref wc);
    }

    private void EnsureLineWindow()
    {
        if (_lineHwnd != IntPtr.Zero) return;

        uint exStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        _lineHwnd = Native.CreateWindowExW(exStyle, LineClass, "ClipLine P0 line", Native.WS_POPUP,
            0, 0, _screenWidth, PanelHeight, IntPtr.Zero, IntPtr.Zero,
            Native.GetModuleHandleW(null), IntPtr.Zero);

        if (_lineHwnd == IntPtr.Zero)
        {
            Check("创建自管分层 HWND", false, "CreateWindowExW 失败，Win32Error=" + Marshal.GetLastWin32Error());
            return;
        }

        Native.SetWindowPos(_lineHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _transparent = true;
        _canvas.Render(false);
        _canvas.Present(_lineHwnd, 0, 0);
    }

    /// <summary>状态机单步。坐标与时间都由调用方注入，因此自检可用假时钟、不动真实鼠标。</summary>
    internal void Tick(int px, int py, long nowMs)
    {
        _now = nowMs;

        bool inHotBand = py >= 0 && py <= HotBand
            && px >= _screenWidth / 3 && px <= 2 * _screenWidth / 3;
        bool inKeepArea = py <= PanelHeight; // 判定区：从线最低点到屏幕顶

        if (inHotBand)
        {
            if (_hotZoneSince < 0) _hotZoneSince = nowMs;
            if (!_revealed && nowMs - _hotZoneSince >= RevealDelay) SetRevealed(true);
        }
        else _hotZoneSince = -1;

        if (_revealed)
        {
            if (inKeepArea) _awaySince = -1;
            else
            {
                if (_awaySince < 0) _awaySince = nowMs;
                if (nowMs - _awaySince >= RetractDelay) SetRevealed(false);
            }
        }

        if (!_revealed && _collapsedAt >= 0 && nowMs - _collapsedAt >= DestroyDelay
            && _lineHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_lineHwnd);
            _lineHwnd = IntPtr.Zero;
        }

        if (_lineHwnd == IntPtr.Zero) return;
        bool hitsCard = _revealed
            && px >= _canvas.CardLeft - 4 && px <= _canvas.CardLeft + RopeCanvas.CardWidth + 4
            && py >= _canvas.CardTop - 4 && py <= _canvas.CardTop + RopeCanvas.CardHeight + 4;
        SetTransparent(!hitsCard);
    }

    private void SetRevealed(bool value)
    {
        if (_revealed == value) return;
        _revealed = value;
        if (value)
        {
            EnsureLineWindow();
            if (_lineHwnd == IntPtr.Zero) return;
            Native.ShowWindow(_lineHwnd, Native.SW_SHOWNOACTIVATE);
            _canvas.Render(false);
            _canvas.Present(_lineHwnd, 0, 0);
            _collapsedAt = -1;
        }
        else
        {
            if (_lineHwnd != IntPtr.Zero) Native.ShowWindow(_lineHwnd, Native.SW_HIDE);
            _collapsedAt = _now;
        }
    }

    private void SetTransparent(bool value)
    {
        if (_lineHwnd == IntPtr.Zero || _transparent == value) return;
        long style = Native.GetWindowLongPtrW(_lineHwnd, Native.GWL_EXSTYLE).ToInt64();
        long updated = value
            ? style | Native.WS_EX_TRANSPARENT
            : style & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtrW(_lineHwnd, Native.GWL_EXSTYLE, new IntPtr(updated));
        Native.SetWindowPos(_lineHwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        _transparent = value;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_TIMER:
                Native.GetCursorPos(out var pt);
                Tick(pt.X, pt.Y, Environment.TickCount64);
                return IntPtr.Zero;

            case TrayIcon.CallbackMessage:
                uint mouseMsg = (uint)lParam.ToInt64() & 0xFFFF;
                if (mouseMsg == 0x0205) // WM_RBUTTONUP
                    Native.PostQuitMessage(0);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                if (hWnd == _msgHwnd) Native.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void RunSelfTest()
    {
        int midX = _screenWidth / 2;
        int cardX = _canvas.CardLeft + RopeCanvas.CardWidth / 2;
        int cardY = _canvas.CardTop + RopeCanvas.CardHeight / 2;

        // --- 窗口样式与平台能力 ---
        long ex = _lineHwnd == IntPtr.Zero ? 0
            : Native.GetWindowLongPtrW(_lineHwnd, Native.GWL_EXSTYLE).ToInt64();
        bool stylesOk = (ex & Native.WS_EX_LAYERED) != 0
            && (ex & Native.WS_EX_NOACTIVATE) != 0
            && (ex & Native.WS_EX_TOOLWINDOW) != 0;
        Check("自管分层 HWND 样式 (LAYERED|NOACTIVATE|TOOLWINDOW)", _lineHwnd != IntPtr.Zero && stylesOk,
            $"hwnd=0x{_lineHwnd.ToInt64():X}, exStyle=0x{ex:X}");
        Check("初始整窗点击穿透 (WS_EX_TRANSPARENT)", (ex & Native.WS_EX_TRANSPARENT) != 0, "");
        Check("不进 Alt+Tab / 任务栏 (TOOLWINDOW 且无 APPWINDOW)",
            (ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0, "");
        Check("GDI 路径不带 WS_EX_NOREDIRECTIONBITMAP",
            (ex & Native.WS_EX_NOREDIRECTIONBITMAP) == 0,
            "该样式属于产品级 DirectComposition 路径，与 UpdateLayeredWindow 互斥");

        _canvas.Render(false);
        string previewPath = Path.Combine(AppContext.BaseDirectory, "p0-preview.bmp");
        _canvas.DumpPreviewBmp(previewPath);
        bool presented = _canvas.Present(_lineHwnd, 0, 0);
        Check("UpdateLayeredWindow 上屏", presented,
            presented ? $"预览图 {previewPath}" : "Win32Error=" + Marshal.GetLastWin32Error());

        // --- 热区 0.25 s ---
        Tick(midX, 5, 0);
        Tick(midX, 5, 100);
        bool notRevealedAt100 = !_revealed;
        Tick(midX, 5, 260);
        Check("顶部中间 1/3 热区停留 0.25 s 才展开", notRevealedAt100 && _revealed,
            "假时钟 t=0/100/260 ms");

        // --- 局部命中 ---
        Tick(cardX, cardY, 300);
        long exOnCard = Native.GetWindowLongPtrW(_lineHwnd, Native.GWL_EXSTYLE).ToInt64();
        IntPtr hitOnCard = Native.WindowFromPoint(new Native.POINT(cardX, cardY));
        Check("卡片命中时清除 WS_EX_TRANSPARENT", (exOnCard & Native.WS_EX_TRANSPARENT) == 0,
            $"命中点 ({cardX},{cardY})");
        Check("卡片区可被 WindowFromPoint 命中", hitOnCard == _lineHwnd,
            $"返回 0x{hitOnCard.ToInt64():X}，期望 0x{_lineHwnd.ToInt64():X}");

        Tick(10, 5, 350);
        long exOnBlank = Native.GetWindowLongPtrW(_lineHwnd, Native.GWL_EXSTYLE).ToInt64();
        IntPtr hitOnBlank = Native.WindowFromPoint(new Native.POINT(10, 5));
        Check("空白区恢复 WS_EX_TRANSPARENT", (exOnBlank & Native.WS_EX_TRANSPARENT) != 0, "");
        Check("空白区点击穿透到下层窗口", hitOnBlank != _lineHwnd,
            $"返回 0x{hitOnBlank.ToInt64():X}");

        // --- 收起 0.5 s / 销毁 0.3 s ---
        Tick(midX, 400, 400);
        Tick(midX, 400, 700);
        bool stillRevealedAt300 = _revealed;
        Tick(midX, 400, 950);
        Check("离开判定区 0.5 s 才收起", stillRevealedAt300 && !_revealed,
            "假时钟 t=400/700/950 ms");

        Tick(midX, 400, 1100);
        bool aliveAt150 = _lineHwnd != IntPtr.Zero;
        Tick(midX, 400, 1300);
        Check("收起 0.3 s 后 DestroyWindow", aliveAt150 && _lineHwnd == IntPtr.Zero,
            "假时钟 t=1100/1300 ms");

        bool leftover = false;
        Native.EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256);
            Native.GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() == LineClass) leftover = true;
            return true;
        }, IntPtr.Zero);
        Check("EnumWindows 找不到残留线窗口", !leftover, "");

        // --- 收起后重新唤出：窗口必须能重建，托盘仍然存活 ---
        Tick(midX, 5, 1400);
        Tick(midX, 5, 1700);
        Check("收起后可从热区重新创建线窗口", _revealed && _lineHwnd != IntPtr.Zero,
            $"新 hwnd=0x{_lineHwnd.ToInt64():X}");
        Check("托盘宿主与瞬时线窗口解耦", _msgHwnd != IntPtr.Zero && _trayIcon != IntPtr.Zero,
            "托盘挂在 message-only 窗口上，不随线窗口销毁");
    }

    private void RunInteractive()
    {
        Console.WriteLine("托盘图标已就绪。把鼠标移到屏幕顶边中间 1/3 停 0.25 秒唤出绳；");
        Console.WriteLine("移到卡片上可点，离开 0.5 秒收起；右键托盘图标退出。");
        Console.WriteLine();

        Native.SetTimer(_msgHwnd, new UIntPtr(1), FrameMs, IntPtr.Zero);
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    private void Cleanup()
    {
        if (_msgHwnd != IntPtr.Zero)
        {
            Native.KillTimer(_msgHwnd, new UIntPtr(1));
            TrayIcon.Remove(_msgHwnd);
        }
        if (_trayIcon != IntPtr.Zero) Native.DestroyIcon(_trayIcon);
        if (_lineHwnd != IntPtr.Zero) Native.DestroyWindow(_lineHwnd);
        if (_msgHwnd != IntPtr.Zero) Native.DestroyWindow(_msgHwnd);
        _lineHwnd = IntPtr.Zero;
        _msgHwnd = IntPtr.Zero;
        _canvas?.Dispose();
    }
}
