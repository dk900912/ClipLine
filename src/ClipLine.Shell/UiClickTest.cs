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
            MakePng(path, palette[i]);
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

    private static void MakePng(string path, (byte B, byte G, byte R) color)
    {
        const int w = 320, h = 200;
        byte[] bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bgra[i * 4] = color.B;
            bgra[i * 4 + 1] = color.G;
            bgra[i * 4 + 2] = color.R;
            bgra[i * 4 + 3] = 255;
        }
        GdiPlus.SaveBgraAsPng(bgra, w, h, path);
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
            Thread.Sleep(260);
            dragged = true;
        }
        int shapes = 0;
        OnUi(() => shapes = s_host!.Tools.Get(ToolMode.Markup)?.ShapeCount ?? 0);
        Check("标记 → 真实鼠标拖拽画出 1 条标注（可编辑，不是打开看图程序）",
            dragged && shapes == 1, $"拖拽={dragged} 形状数={shapes}");
        string marked = Path.Combine(s_root, "frame-markup-drawn.png");
        OnUi(() => s_host!.Tools.DumpFrame(ToolMode.Markup, marked));
        Evidence.Add(marked);
        CloseTool(ToolMode.Markup);
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
