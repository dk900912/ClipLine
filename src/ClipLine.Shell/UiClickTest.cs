using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>
/// 真实 UI 点击端到端验证（--ui-click）。
///
/// 与 --smoke / --menu-audit 的区别：那两个是「假时钟 + 直接调用门面」的确定性断言；
/// 这里在<strong>真实桌面进程</strong>里，从独立线程用 mouse_event / keybd_event 把输入
/// 注入系统队列，走完整链路：
///   SetCursorPos → WM_RBUTTONUP → WndProc → 输入桥 → 门面 → TrackPopupMenuEx
///   → FindWindow(#32768) + MN_GETHMENU + GetMenuItemRect → 真实左键点击菜单项
///   → 门面命令 → 自绘窗口 / 剪贴板 / 固定状态。
/// 每一步都记录物理坐标、菜单项文字、窗口可见性，并导出窗口帧与整屏截图作为证据。
///
/// 夹具（3 张 PNG 挂上绳）通过 UI 线程准备；所有 host 写操作都 Post 回 UI 线程，
/// 避免后台线程创建 HWND 导致消息泵错线程。
/// </summary>
public static class UiClickTest
{
    private static readonly List<(string Name, bool Pass, string Detail)> Checks = new();
    private static readonly List<string> Evidence = new();
    private static ShellAppHost? s_host;
    private static SynchronizationContext? s_ui;
    private static string s_root = string.Empty;
    private static int s_screenW;
    private static int s_screenH;

    private static void Check(string name, bool pass, string detail = "") =>
        Checks.Add((name, pass, detail));

    public static void Start(ShellAppHost host, SynchronizationContext ui)
    {
        s_host = host;
        s_ui = ui;
        var thread = new Thread(Run) { IsBackground = true, Name = "ClipLine.UiClick" };
        thread.Start();
    }

    private static void OnUi(Action action)
    {
        SynchronizationContext? ui = s_ui;
        if (ui is null) { action(); return; }
        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        ui.Post(_ =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        }, null);
        if (!done.Wait(TimeSpan.FromSeconds(12)))
            throw new TimeoutException("UI 线程 12 秒内没有处理测试动作");
        if (failure is not null) throw failure;
    }

    private static void Run()
    {
        s_root = Path.Combine(Path.GetTempPath(), "clipline-uiclick");
        Directory.CreateDirectory(s_root);
        string reportPath = Path.Combine(s_root, "ui-click-result.txt");
        s_screenW = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
        s_screenH = ShellNative.GetSystemMetrics(ShellNative.SM_CYSCREEN);
        int passed = 0;

        try
        {
            PrepareFixture();
            Thread.Sleep(450);

            // 绳子由真实进程显示，卡片命中矩形来自真实分层 HWND 的画布。
            bool visible = false;
            int cards = 0;
            OnUi(() =>
            {
                visible = s_host!.Rope.IsVisible;
                cards = s_host.Rope.CardRects.Count;
            });
            Check("夹具：真实桌面显示绳窗口且挂上 3 张图片", visible && cards >= 3,
                $"可见={visible} 卡片={cards}");

            VerifyQuickLook();
            VerifyMarkup();
            VerifyHeld();
            VerifyPin();
            VerifyCopy();
            VerifyDragOut();
            VerifyUnpin();
            VerifyOpen();
        }
        catch (Exception ex)
        {
            Check("端到端点击过程无异常", false, ex.GetType().Name + ": " + ex.Message);
        }

        var report = new StringBuilder();
        report.AppendLine("ClipLine 真实鼠标点击端到端验证 (--ui-click)");
        report.AppendLine("输入方式：mouse_event / keybd_event 注入系统队列，非直接调用门面");
        report.AppendLine($"屏幕：{s_screenW}×{s_screenH}  证据目录：{s_root}");
        report.AppendLine(new string('-', 72));
        foreach ((string name, bool pass, string detail) in Checks)
        {
            if (pass) passed++;
            report.AppendLine($"  [{(pass ? "PASS" : "FAIL")}] {name}" +
                (detail.Length > 0 ? " — " + detail : ""));
        }
        report.AppendLine(new string('-', 72));
        foreach (string path in Evidence) report.AppendLine("  EVIDENCE " + path);
        report.AppendLine();
        report.AppendLine($"VERDICT: {(passed == Checks.Count ? "PASS" : "FAIL")} ({passed}/{Checks.Count})");

        string text = report.ToString();
        try { File.WriteAllText(reportPath, text, new UTF8Encoding(false)); }
        catch { /* 报告写不出去时仍按断言结果退出 */ }
        try { Console.WriteLine(text); } catch { /* WinExe 无控制台 */ }

        Environment.Exit(passed == Checks.Count ? 0 : 1);
    }

    // ------------------------------------------------------------------
    // 夹具
    // ------------------------------------------------------------------

    private static void PrepareFixture()
    {
        // 上一轮若在 OLE 拖放中途异常退出，系统会保留「左键仍按下」的输入状态，
        // 之后所有右键菜单都会被当成「拖动中的第二次按键」而忽略。
        // 夹具先补发一次抬起，把输入队列恢复到干净状态。
        ShellNative.mouse_event(ShellNative.MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        ShellNative.mouse_event(ShellNative.MouseeventfRightup, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);

        // 标注草稿是「崩溃后可恢复」的兜底（P0-2），会按原图路径在下次打开时自动回填。
        // 同一个夹具图片路径固定，上一轮遗留的草稿会让形状数凭空 +1，必须先清掉。
        try
        {
            string draft = Path.Combine(Path.GetTempPath(), "ClipLine", "markup-draft.json");
            if (File.Exists(draft)) File.Delete(draft);
        }
        catch { /* 草稿删不掉不影响后续步骤 */ }

        var paths = new List<string>();
        var palette = new (byte B, byte G, byte R)[]
        {
            (0xD5, 0x5B, 0x4F), // 靛蓝 #4F5BD5
            (0x24, 0x8A, 0xEF), // 暖橙 #EF8A24
            (0xE0, 0xA9, 0x2A), // 天蓝 #2AA9E0
        };
        for (int i = 0; i < palette.Length; i++)
        {
            string path = Path.Combine(s_root, $"uiclick-{i}.png");
            // 第一张做双色斜条纹：马赛克这类「按像素」的效果在纯色图上根本看不出来，
            // 而标注窗默认打开的就是第 0 张。
            MakePng(path, palette[i], i == 0 ? palette[1] : null);
            paths.Add(path);
        }
        OnUi(() =>
        {
            ShellAppHost host = s_host!;
            foreach (string path in paths)
                host.Facade.Handle(new NewItem(path, ItemOrigin.Clipboard, InOwnDirectory: false));
            host.AdvanceFrame();
            host.EnsurePresented();
            host.RenderFrame();
            host.Tray.Notice?.Hide(); // 首次欢迎通知不参与本次 UI 验证
        });
    }

    private static void MakePng(string path, (byte B, byte G, byte R) color,
        (byte B, byte G, byte R)? second = null)
    {
        const int w = 320, h = 200;
        byte[] bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (byte B, byte G, byte R) c = second is not null && ((x + y) / 26) % 2 == 1
                    ? second.Value : color;
                int i = y * w + x;
                bgra[i * 4] = c.B;
                bgra[i * 4 + 1] = c.G;
                bgra[i * 4 + 2] = c.R;
                bgra[i * 4 + 3] = 255;
            }
        GdiPlus.SaveBgraAsPng(bgra, w, h, path);
    }

    /// <summary>
    /// 等模型里的标注条数到达预期。注入的鼠标事件进的是系统输入队列，
    /// 队列忙的时候抬起会比调用晚几百毫秒才被处理 —— 固定 sleep 会读到旧值。
    /// </summary>
    private static bool WaitShapeCount(ToolMode mode, int expected, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int seen = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            OnUi(() => seen = s_host!.Tools.Get(mode)?.ShapeCount ?? -1);
            if (seen == expected) return true;
            Thread.Sleep(60);
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 真实输入原语
    // ------------------------------------------------------------------

    private static void MouseMove(int x, int y) => ShellNative.SetCursorPos(x, y);

    private static void LeftClick(int x, int y)
    {
        MouseMove(x, y);
        Thread.Sleep(60);
        ShellNative.mouse_event(ShellNative.MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(35);
        ShellNative.mouse_event(ShellNative.MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
    }

    private static void RightClick()
    {
        ShellNative.mouse_event(ShellNative.MouseeventfRightdown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(35);
        ShellNative.mouse_event(ShellNative.MouseeventfRightup, 0, 0, 0, UIntPtr.Zero);
    }

    private static void PressKey(byte vk)
    {
        ShellNative.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(35);
        ShellNative.keybd_event(vk, 0, ShellNative.KeyeventfKeyup, UIntPtr.Zero);
    }

    private static (int X, int Y)? CardCenter()
    {
        (int X, int Y)? result = null;
        OnUi(() =>
        {
            var rects = s_host!.Rope.CardRects;
            if (rects.Count > 0)
            {
                var r = rects[0];
                result = (r.X + r.W / 2, r.Y + r.H / 2);
            }
        });
        return result;
    }

    private static bool WaitForRope(int timeoutMs)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            bool ok = false;
            OnUi(() => ok = s_host!.Rope.IsVisible);
            if (ok) return true;
            Thread.Sleep(45);
        }
        return false;
    }

    private static IntPtr WaitForMenu(int timeoutMs)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            IntPtr hwnd = ShellNative.FindWindowW("#32768", null);
            if (hwnd != IntPtr.Zero && ShellNative.IsWindowVisible(hwnd)) return hwnd;
            Thread.Sleep(25);
        }
        return IntPtr.Zero;
    }

    private static void WaitMenuGone(IntPtr menu, int timeoutMs)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (!ShellNative.IsWindow(menu)) return;
            Thread.Sleep(25);
        }
    }

    private static int FindMenuItem(IntPtr hMenu, string label)
    {
        if (hMenu == IntPtr.Zero) return -1;
        int count = ShellNative.GetMenuItemCount(hMenu);
        var sb = new StringBuilder(256);
        for (int i = 0; i < count; i++)
        {
            sb.Clear();
            int len = ShellNative.GetMenuStringW(hMenu, (uint)i, sb, sb.Capacity,
                ShellNative.MfByPosition);
            if (len <= 0) continue;
            if (sb.ToString().Contains(label, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static (int X, int Y)? MenuItemCenter(IntPtr menuHwnd, IntPtr hMenu, int index)
    {
        if (ShellNative.GetMenuItemRect(menuHwnd, hMenu, (uint)index, out ShellNative.RECT r)
            && r.Right > r.Left && r.Bottom > r.Top)
            return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        if (ShellNative.GetMenuItemRect(IntPtr.Zero, hMenu, (uint)index, out r)
            && r.Right > r.Left && r.Bottom > r.Top)
            return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        if (ShellNative.GetWindowRect(menuHwnd, out r))
        {
            int itemH = Math.Max(18, ShellNative.GetSystemMetrics(ShellNative.SmCyMenu));
            return ((r.Left + r.Right) / 2, r.Top + (int)((index + 0.5) * itemH));
        }
        return null;
    }

    /// <summary>真实右键卡片 → 按菜单文字定位 → 真实左键点击；返回点击坐标等细节。</summary>
    private static bool ClickCardMenu(string label, out string detail)
    {
        detail = string.Empty;
        // 每一步都先真实把鼠标移到屏幕顶部中间热区，让被工具窗口/放回动作收起的
        // 绳子重新展开（顺带覆盖「空绳也能被热区唤出」）。
        MouseMove(s_screenW / 2, 2);
        Thread.Sleep(360);
        if (!WaitForRope(1800))
        {
            detail = "顶部热区唤出后绳窗口仍不可见";
            return false;
        }
        (int X, int Y)? card = CardCenter();
        if (card is null) { detail = "绳上没有卡片"; return false; }
        MouseMove(card.Value.X, card.Value.Y);
        Thread.Sleep(340); // 等 30fps 帧驱动把窗口从 passthrough 切成可命中
        RightClick();

        IntPtr menu = WaitForMenu(3500);
        if (menu == IntPtr.Zero) { detail = "右键没有弹出 Win32 菜单 (#32768)"; return false; }
        IntPtr hMenu = ShellNative.SendMessageW(menu, ShellNative.MnGetHMenu,
            UIntPtr.Zero, IntPtr.Zero);
        int index = FindMenuItem(hMenu, label);
        if (index < 0)
        {
            detail = $"菜单里找不到「{label}」(hMenu=0x{hMenu.ToInt64():X})";
            PressKey(ShellNative.VkEscape);
            WaitMenuGone(menu, 1500);
            return false;
        }
        (int X, int Y)? point = MenuItemCenter(menu, hMenu, index);
        if (point is null)
        {
            detail = $"取不到「{label}」的菜单项矩形";
            PressKey(ShellNative.VkEscape);
            WaitMenuGone(menu, 1500);
            return false;
        }
        LeftClick(point.Value.X, point.Value.Y);
        WaitMenuGone(menu, 2500);
        Thread.Sleep(380); // 让 UI 线程处理门面命令并呈现窗口
        detail = $"卡片=({card.Value.X},{card.Value.Y}) " +
            $"菜单项[{index}]「{label}」 点击=({point.Value.X},{point.Value.Y})";
        return true;
    }

    // ------------------------------------------------------------------
    // 证据
    // ------------------------------------------------------------------

    private static bool CaptureScreen(string path)
    {
        IntPtr screen = ShellNative.GetDC(IntPtr.Zero);
        IntPtr mem = ShellNative.CreateCompatibleDC(screen);
        IntPtr bmp = ShellNative.CreateCompatibleBitmap(screen, s_screenW, s_screenH);
        if (screen == IntPtr.Zero || mem == IntPtr.Zero || bmp == IntPtr.Zero) return false;
        IntPtr old = ShellNative.SelectObject(mem, bmp);
        bool ok = ShellNative.BitBlt(mem, 0, 0, s_screenW, s_screenH,
            screen, 0, 0, 0x00CC0020); // SRCCOPY
        ShellNative.SelectObject(mem, old);
        bool saved = ok && GdiPlus.SaveBitmapAsPng(bmp, path);
        ShellNative.DeleteObject(bmp);
        ShellNative.DeleteDC(mem);
        ShellNative.ReleaseDC(IntPtr.Zero, screen);
        return saved && File.Exists(path);
    }

    private static (IntPtr Hwnd, int X, int Y, int W, int H)? ToolButton(ToolMode mode, string which)
    {
        (IntPtr Hwnd, int X, int Y, int W, int H)? info = null;
        OnUi(() =>
        {
            ToolWindow? win = s_host!.Tools.Get(mode);
            if (win is null || !win.IsVisible) return;
            var r = which switch
            {
                "close" => win.Layout.Close,
                "canvas" => win.Layout.Canvas,
                "save" => win.Layout.Save,
                "putback" => win.Layout.PutBack,
                "confirmdiscard" => win.Layout.ConfirmDiscard,
                "text" => win.Layout.Tools.Count > MarkShape.KindText
                    ? win.Layout.Tools[MarkShape.KindText] : win.Layout.Close,
                "mosaic" => win.Layout.Tools.Count > MarkShape.KindMosaic
                    ? win.Layout.Tools[MarkShape.KindMosaic] : win.Layout.Close,
                _ => win.Layout.Close,
            };
            info = (win.Handle, r.X, r.Y, r.W, r.H);
        });
        return info;
    }

    private static (int X, int Y)? ButtonScreenPoint(
        (IntPtr Hwnd, int X, int Y, int W, int H) info)
    {
        if (!ShellNative.GetWindowRect(info.Hwnd, out ShellNative.RECT wr)) return null;
        return (wr.Left + info.X + info.W / 2, wr.Top + info.Y + info.H / 2);
    }

    private static void CloseTool(ToolMode mode)
    {
        var info = ToolButton(mode, "close");
        if (info is null) return;
        (int X, int Y)? pt = ButtonScreenPoint(info.Value);
        if (pt is not null) LeftClick(pt.Value.X, pt.Value.Y);
        Thread.Sleep(280);
    }

    private static void VerifyToolWindow(ToolMode mode, string label)
    {
        string frame = Path.Combine(s_root, "frame-" + mode + ".png");
        string screen = Path.Combine(s_root, "screen-" + mode + ".png");
        bool visible = false, dumped = false;
        OnUi(() =>
        {
            visible = s_host!.Tools.IsVisible(mode);
            dumped = s_host.Tools.DumpFrame(mode, frame);
        });
        Check($"{label} → 独立自绘 {mode} 窗口出现并导出帧",
            visible && dumped && File.Exists(frame) && new FileInfo(frame).Length > 1000,
            $"可见={visible} 帧={dumped} 大小={(File.Exists(frame) ? new FileInfo(frame).Length : 0)} B");
        bool centered = false;
        string rectText = "无窗口矩形";
        OnUi(() =>
        {
            ToolWindow? win = s_host!.Tools.Get(mode);
            if (win is not null && ShellNative.GetWindowRect(win.Handle, out ShellNative.RECT wr))
            {
                rectText = $"({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom})";
                int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
                centered = Math.Abs(cx - s_screenW / 2) < s_screenW / 4
                    && Math.Abs(cy - s_screenH / 2) < s_screenH / 3
                    && wr.Left > 0 && wr.Top > 0;
            }
        });
        Check($"{label} → 窗口居中且未被分层窗口目标点拉回左上角", centered, rectText);

        bool shot = CaptureScreen(screen);
        Check($"{label} → 整屏截图证明窗口真的在桌面上",
            shot && new FileInfo(screen).Length > 5000,
            screen);
        Evidence.Add(frame);
        Evidence.Add(screen);
    }

    // ------------------------------------------------------------------
    // 各菜单项
    // ------------------------------------------------------------------

    private static void VerifyQuickLook()
    {
        bool clicked = ClickCardMenu("快速查看", out string detail);
        if (!clicked) { Check("真实点击「快速查看」", false, detail); return; }
        Check("真实点击「快速查看」", true, detail);
        VerifyToolWindow(ToolMode.QuickLook, "快速查看");

        int before = -1, after = -1;
        OnUi(() => before = s_host!.Tools.Get(ToolMode.QuickLook)?.Index ?? -1);
        PressKey(ShellNative.VkRight);
        Thread.Sleep(220);
        OnUi(() => after = s_host!.Tools.Get(ToolMode.QuickLook)?.Index ?? -1);
        Check("快速查看 → 真实按 → 切换到下一张（不再是同一个看图程序）",
            before >= 0 && after >= 0 && after != before, $"{before} → {after}");
        CloseTool(ToolMode.QuickLook);
    }

    private static void VerifyMarkup()
    {
        bool clicked = ClickCardMenu("标记", out string detail);
        if (!clicked) { Check("真实点击「标记」", false, detail); return; }
        Check("真实点击「标记」", true, detail);
        VerifyToolWindow(ToolMode.Markup, "标记");

        var canvas = ToolButton(ToolMode.Markup, "canvas");
        bool dragged = false;
        if (canvas is not null && ShellNative.GetWindowRect(canvas.Value.Hwnd,
                out ShellNative.RECT wr))
        {
            int x0 = wr.Left + canvas.Value.X + canvas.Value.W / 3;
            int y0 = wr.Top + canvas.Value.Y + canvas.Value.H / 3;
            int x1 = wr.Left + canvas.Value.X + canvas.Value.W * 2 / 3;
            int y1 = wr.Top + canvas.Value.Y + canvas.Value.H * 2 / 3;
            MouseMove(x0, y0);
            Thread.Sleep(80);
            ShellNative.mouse_event(ShellNative.MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
            for (int i = 1; i <= 8; i++)
            {
                MouseMove(x0 + (x1 - x0) * i / 8, y0 + (y1 - y0) * i / 8);
                Thread.Sleep(18);
            }
            ShellNative.mouse_event(ShellNative.MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
            dragged = WaitShapeCount(ToolMode.Markup, 1);
        }
        int shapes = 0;
        OnUi(() => shapes = s_host!.Tools.Get(ToolMode.Markup)?.ShapeCount ?? 0);
        Check("标记 → 真实鼠标拖拽画出 1 条标注（可编辑，不是打开看图程序）",
            dragged && shapes == 1, $"拖拽={dragged} 形状数={shapes}");
        string marked = Path.Combine(s_root, "frame-markup-drawn.png");
        OnUi(() => s_host!.Tools.DumpFrame(ToolMode.Markup, marked));
        Evidence.Add(marked);

        VerifyMarkupAnnotations();

        // P0-2：带未保存标注点关闭，必须先出现三选一，不能直接丢内容。
        CloseTool(ToolMode.Markup);
        bool stillOpen = false, confirming = false;
        OnUi(() =>
        {
            ToolWindow? win = s_host!.Tools.Get(ToolMode.Markup);
            stillOpen = win?.IsVisible ?? false;
            confirming = win?.IsConfirming ?? false;
        });
        Check("标记 → 有未保存内容时关闭先弹「保存/丢弃/取消」", stillOpen && confirming,
            $"仍可见={stillOpen} 确认中={confirming}");
        string confirmShot = Path.Combine(s_root, "frame-markup-confirm.png");
        OnUi(() => s_host!.Tools.DumpFrame(ToolMode.Markup, confirmShot));
        Evidence.Add(confirmShot);

        var discard = ToolButton(ToolMode.Markup, "confirmdiscard");
        if (discard is not null)
        {
            (int X, int Y)? pt = ButtonScreenPoint(discard.Value);
            if (pt is not null) { LeftClick(pt.Value.X, pt.Value.Y); Thread.Sleep(220); }
        }
        bool closed = false;
        OnUi(() => closed = !s_host!.Tools.IsVisible(ToolMode.Markup));
        Check("标记 → 选择「丢弃」后窗口才关闭", closed, $"已关闭={closed}");
    }

    /// <summary>
    /// P1-10：两种新笔型必须真能落下 ——
    /// 文字走原生输入窗（中文输入法、选区、粘贴都可用），马赛克按区域块化底图。
    /// 断言读的是模型里的形状串，不是"画面上好像有东西"。
    /// </summary>
    private static void VerifyMarkupAnnotations()
    {
        string before = string.Empty;
        OnUi(() => before = s_host!.Tools.Get(ToolMode.Markup)?.ShapeSummary() ?? string.Empty);
        int beforeCount = before.Length == 0 ? 0 : before.Split(';').Length;

        // ① 文字
        var textTool = ToolButton(ToolMode.Markup, "text");
        var canvas = ToolButton(ToolMode.Markup, "canvas");
        bool textOk = false;
        string textDetail = "工具按钮缺失";
        if (textTool is not null && canvas is not null
            && ShellNative.GetWindowRect(canvas.Value.Hwnd, out ShellNative.RECT cr))
        {
            (int X, int Y)? tool = ButtonScreenPoint(textTool.Value);
            if (tool is not null) { LeftClick(tool.Value.X, tool.Value.Y); Thread.Sleep(140); }
            int ax = cr.Left + canvas.Value.X + canvas.Value.W / 2;
            int ay = cr.Top + canvas.Value.Y + canvas.Value.H / 2;
            LeftClick(ax, ay);

            IntPtr input = IntPtr.Zero;
            for (int i = 0; i < 30 && input == IntPtr.Zero; i++)
            {
                input = ShellNative.FindWindowW("ClipLine_TextInput", null);
                if (input == IntPtr.Zero) Thread.Sleep(50);
            }
            if (input == IntPtr.Zero) textDetail = "输入窗没有出现";
            else
            {
                // 窗口刚创建、还没走到 WM_PAINT；立刻截屏只能拍到它下面那张脸。
                Thread.Sleep(320);
                IntPtr edit = ShellNative.FindWindowExW(input, IntPtr.Zero, "EDIT", null);
                if (edit == IntPtr.Zero) textDetail = "输入窗里没有 EDIT 控件";
                else
                {
                    // 客户区布局回归：标题栏高度由系统决定，用「整窗高度 − 常数」算出来的
                    // EDIT 会顶出客户区、压到按钮上。这里量的是真实客户区坐标。
                    bool inside = false;
                    string insideText = "拿不到窗口矩形";
                    if (ShellNative.GetWindowRect(edit, out ShellNative.RECT er)
                        && ShellNative.GetClientRect(input, out ShellNative.RECT ic))
                    {
                        var p0 = new ShellNative.POINT(er.Left, er.Top);
                        var p1 = new ShellNative.POINT(er.Right, er.Bottom);
                        ShellNative.ScreenToClient(input, ref p0);
                        ShellNative.ScreenToClient(input, ref p1);
                        inside = p0.X >= 0 && p0.Y >= 0
                            && p1.X <= ic.Right && p1.Y <= ic.Bottom;
                        insideText = $"EDIT=({p0.X},{p0.Y})-({p1.X},{p1.Y}) " +
                            $"客户区={ic.Right}×{ic.Bottom}";
                    }
                    Check("标记 → 文字输入窗：EDIT 完整落在客户区内（标题栏高度不写死）",
                        inside, insideText);

                    string inputShot = Path.Combine(s_root, "screen-markup-textinput.png");
                    bool inputShotOk = CaptureScreen(inputShot);
                    Evidence.Add(inputShot);
                    Check("标记 → 文字输入窗整屏截图（中文输入与按钮可见）",
                        inputShotOk && File.Exists(inputShot) && new FileInfo(inputShot).Length > 5000,
                        inputShot);

                    ShellNative.SetWindowTextW(edit, "打码测试");
                    Thread.Sleep(90);
                    // Enter 发给输入窗自身：与点「确定」走同一条 Finish(true) 路径。
                    ShellNative.SendMessageW(input, ShellNative.WM_KEYDOWN,
                        (UIntPtr)ShellNative.VkEnter, IntPtr.Zero);
                    WaitShapeCount(ToolMode.Markup, beforeCount + 1);
                    string after = string.Empty;
                    OnUi(() => after = s_host!.Tools.Get(ToolMode.Markup)?.ShapeSummary() ?? string.Empty);
                    int count = after.Length == 0 ? 0 : after.Split(';').Length;
                    textOk = count == beforeCount + 1
                        && after.Contains($"4:打码测试:", StringComparison.Ordinal);
                    textDetail = $"{beforeCount}→{count} 内容={after}";
                }
            }
        }
        Check("标记 → 文字笔型：真实打开输入窗、中文落字进模型", textOk, textDetail);
        string textShot = Path.Combine(s_root, "frame-markup-text.png");
        OnUi(() => s_host!.Tools.DumpFrame(ToolMode.Markup, textShot));
        Evidence.Add(textShot);

        // ② 马赛克
        int countBefore = 0;
        OnUi(() => countBefore = s_host!.Tools.Get(ToolMode.Markup)?.ShapeCount ?? 0);
        bool mosaicOk = false;
        string mosaicDetail = "工具按钮缺失";
        var mosaicTool = ToolButton(ToolMode.Markup, "mosaic");
        if (mosaicTool is not null && canvas is not null
            && ShellNative.GetWindowRect(canvas.Value.Hwnd, out ShellNative.RECT cr2))
        {
            (int X, int Y)? tool = ButtonScreenPoint(mosaicTool.Value);
            if (tool is not null) { LeftClick(tool.Value.X, tool.Value.Y); Thread.Sleep(140); }
            int x0 = cr2.Left + canvas.Value.X + canvas.Value.W / 3;
            int y0 = cr2.Top + canvas.Value.Y + canvas.Value.H / 3;
            int x1 = cr2.Left + canvas.Value.X + canvas.Value.W / 2;
            int y1 = cr2.Top + canvas.Value.Y + canvas.Value.H / 2;
            MouseMove(x0, y0);
            Thread.Sleep(70);
            ShellNative.mouse_event(ShellNative.MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
            for (int i = 1; i <= 6; i++)
            {
                MouseMove(x0 + (x1 - x0) * i / 6, y0 + (y1 - y0) * i / 6);
                Thread.Sleep(20);
            }
            ShellNative.mouse_event(ShellNative.MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
            WaitShapeCount(ToolMode.Markup, countBefore + 1);
            string summary = string.Empty;
            OnUi(() => summary = s_host!.Tools.Get(ToolMode.Markup)?.ShapeSummary() ?? string.Empty);
            int count = summary.Length == 0 ? 0 : summary.Split(';').Length;
            mosaicOk = count == countBefore + 1
                && summary.Contains("5:", StringComparison.Ordinal);
            mosaicDetail = $"{countBefore}→{count} 内容={summary}";
        }
        Check("标记 → 马赛克笔型：拖出区域后落进模型", mosaicOk, mosaicDetail);
        string mosaicShot = Path.Combine(s_root, "frame-markup-mosaic.png");
        OnUi(() => s_host!.Tools.DumpFrame(ToolMode.Markup, mosaicShot));
        Evidence.Add(mosaicShot);
    }

    private static void VerifyHeld()
    {
        bool clicked = ClickCardMenu("拿在手上", out string detail);
        if (!clicked) { Check("真实点击「拿在手上」", false, detail); return; }
        Check("真实点击「拿在手上」", true, detail);
        VerifyToolWindow(ToolMode.Held, "拿在手上");

        var putBack = ToolButton(ToolMode.Held, "putback");
        bool hasButton = putBack is not null && putBack.Value.W > 0;
        Check("拿在手上 → 浮动窗带「放回绳上」按钮（与快速查看/标记不同）",
            hasButton, putBack is null ? "无按钮" : $"{putBack.Value.W}×{putBack.Value.H}px");
        if (putBack is not null)
        {
            (int X, int Y)? pt = ButtonScreenPoint(putBack.Value);
            if (pt is not null) { LeftClick(pt.Value.X, pt.Value.Y); Thread.Sleep(300); }
        }
        else CloseTool(ToolMode.Held);
        bool closed = false;
        OnUi(() => closed = !s_host!.Tools.IsVisible(ToolMode.Held));
        Check("拿在手上 → 真实点击「放回绳上」后窗口关闭", closed, $"已关闭={closed}");
    }

    private static int KeptCount()
    {
        int kept = -1;
        OnUi(() => kept = s_host!.CurrentView().KeptCount);
        return kept;
    }

    private static void VerifyPin()
    {
        int before = KeptCount();
        string frameBefore = Path.Combine(s_root, "rope-unpinned.png");
        OnUi(() => s_host!.Rope.DumpFrame(frameBefore));
        bool clicked = ClickCardMenu("固定", out string detail);
        if (!clicked) { Check("真实点击「固定」", false, detail); return; }
        int after = KeptCount();
        Check("真实点击「固定」→ 固定数 0→1（状态真的改变）",
            before == 0 && after == 1, $"{before} → {after}；{detail}");

        string frameAfter = Path.Combine(s_root, "rope-pinned-knot.png");
        OnUi(() => s_host!.Rope.DumpFrame(frameAfter));
        bool changed = File.Exists(frameBefore) && File.Exists(frameAfter)
            && !SameBytes(frameBefore, frameAfter);
        Check("固定 → V-09 绳结与淡靛蓝底让绳帧发生可见变化", changed,
            $"前={File.Exists(frameBefore)} 后={File.Exists(frameAfter)} 内容不同={changed}");
        Evidence.Add(frameAfter);

        // 绳结按 mac 规格只有 7×5.5 DIP，全屏帧里很小；再导出一张局部证据方便放大看。
        string knotZoom = Path.Combine(s_root, "knot-zoom.png");
        OnUi(() =>
        {
            var rects = s_host!.Rope.CardRects;
            if (rects.Count < 2) return;
            var a = rects[0];
            var b = rects[1];
            int cx = (a.X + a.W + b.X) / 2;
            double yDip = Layout.RopeY(cx / s_host.Scale, s_screenW / s_host.Scale);
            int cy = (int)Math.Round(yDip * s_host.Scale);
            s_host.Rope.DumpFrame(knotZoom, cx - 70, Math.Max(0, cy - 28), 140, 64);
        });
        Check("固定 → 导出绳结局部放大帧", File.Exists(knotZoom)
            && new FileInfo(knotZoom).Length > 300, knotZoom);
        Evidence.Add(knotZoom);
    }

    private static bool SameBytes(string a, string b)
    {
        try
        {
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }
        catch { return false; }
    }

    private static void VerifyCopy()
    {
        bool clicked = ClickCardMenu("复制", out string detail);
        if (!clicked) { Check("真实点击「复制」", false, detail); return; }
        Thread.Sleep(900); // 门面按系统双击间隔延迟执行复制
        bool hasBitmap = ShellNative.IsClipboardFormatAvailable(ShellNative.CF_BITMAP);
        bool hasDrop = ShellNative.IsClipboardFormatAvailable(ShellNative.CF_HDROP);
        bool anyTool = false;
        OnUi(() => anyTool = s_host!.Tools.Active is not null && s_host.Tools.Active.IsVisible);
        Check("真实点击「复制」→ 剪贴板同时含位图与文件引用（CF_BITMAP + CF_HDROP）",
            hasBitmap && hasDrop, $"bitmap={hasBitmap} hdrop={hasDrop}；{detail}");
        Check("复制 → 不打开任何替身窗口（与「打开」明确区分）", !anyTool,
            $"工具窗可见={anyTool}");
    }

    /// <summary>
    /// P0-5：真的按住卡片拖出窗口，再按 Esc 取消。
    /// 断言三件事：按下并移动确实升级成了 OLE 拖放（DraggingOut）、取消后文件仍在、
    /// 卡片仍在绳上。CF_HDROP 载荷与「去留按磁盘判定」由冒烟确定性覆盖；
    /// 这里证明的是 DoDragDrop 真的被拉起、并且能安全返回。
    /// </summary>
    private static void VerifyDragOut()
    {
        (int X, int Y)? center = CardCenter();
        if (center is null)
        {
            Check("真实拖拽：按住卡片拖出 → Esc 取消后文件与卡片都不受影响", false, "没有卡片");
            return;
        }
        string path = s_host!.Facade.Current.Items[0].Path;
        int before = s_host.Facade.Current.Items.Count;
        bool existsBefore = File.Exists(path);

        MouseMove(center.Value.X, center.Value.Y);
        // 30fps 帧驱动把窗口从 passthrough 切成可命中需要几帧；睡不够的话
        // 这次按下会落到下层桌面上，绳窗口根本收不到 WM_LBUTTONDOWN。
        Thread.Sleep(340);
        ShellNative.mouse_event(ShellNative.MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(70);
        int tx = Math.Max(30, center.Value.X - 640);
        int ty = Math.Clamp(center.Value.Y + 300, 60,
            ShellNative.GetSystemMetrics(ShellNative.SM_CYSCREEN) - 80);
        for (int i = 1; i <= 8; i++)
        {
            MouseMove(center.Value.X + (tx - center.Value.X) * i / 8,
                center.Value.Y + (ty - center.Value.Y) * i / 8);
            Thread.Sleep(40);
        }
        Thread.Sleep(150);
        bool dragging = s_host.Rope.DraggingOut;
        // 「卡片变淡 45%」和拖拽预览只在 DoDragDrop 进行中存在，Esc 一按就没了，
        // 唯一能留证据的时机就是这里。
        string dragShot = Path.Combine(s_root, "screen-drag-out.png");
        bool dragShotOk = CaptureScreen(dragShot);
        Evidence.Add(dragShot);
        Check("真实拖拽：拖放进行中截屏（源卡片变淡 + 指针处有拖拽预览）",
            dragging && dragShotOk && File.Exists(dragShot) && new FileInfo(dragShot).Length > 5000,
            $"{dragShot} 拖放中={dragging}");
        PressKey(ShellNative.VkEscape);
        Thread.Sleep(160);
        ShellNative.mouse_event(ShellNative.MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(500);

        bool existsAfter = File.Exists(path);
        int after = s_host.Facade.Current.Items.Count;
        Check("真实拖拽：按住卡片拖出 → Esc 取消后文件与卡片都不受影响",
            dragging && existsBefore && existsAfter && after == before,
            $"进入拖放={dragging} 文件={existsBefore}->{existsAfter} 卡片={before}->{after}");
    }

    private static void VerifyUnpin()
    {
        bool clicked = ClickCardMenu("取消固定", out string detail);
        if (!clicked) { Check("真实点击「取消固定」", false, detail); return; }
        int after = KeptCount();
        Check("真实点击「取消固定」→ 固定数回到 0、绳结消失", after == 0,
            $"kept={after}；{detail}");
        string frame = Path.Combine(s_root, "rope-unpinned-again.png");
        OnUi(() => s_host!.Rope.DumpFrame(frame));
        Evidence.Add(frame);
    }

    private static List<IntPtr> VisibleTopLevel()
    {
        var list = new List<IntPtr>();
        ShellNative.EnumWindows((hWnd, _) =>
        {
            if (ShellNative.IsWindowVisible(hWnd)) list.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static void VerifyOpen()
    {
        List<IntPtr> before = VisibleTopLevel();
        bool clicked = ClickCardMenu("打开", out string detail);
        if (!clicked) { Check("真实点击「打开」", false, detail); return; }
        Check("真实点击「打开」", true, detail);
        Thread.Sleep(1600);
        List<IntPtr> after = VisibleTopLevel();
        int fresh = 0;
        foreach (IntPtr hwnd in after)
        {
            if (before.Contains(hwnd)) continue;
            var sb = new StringBuilder(128);
            ShellNative.GetClassNameW(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls.StartsWith("ClipLine_", StringComparison.Ordinal)) continue;
            fresh++;
            // 测试不留残留窗口：把系统看图程序礼貌关掉（Post 而非 Send，避免卡住）。
            ShellNative.PostMessageW(hwnd, ShellNative.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
        }
        Check("打开 → 交给系统默认应用（出现新的非 ClipLine 顶层窗口，随后关闭）",
            fresh > 0, $"新外部窗口={fresh}；{detail}");
    }
}
