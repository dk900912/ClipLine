using System.Text;
using ClipLine.Application;
using ClipLine.Domain;
using ClipLine.Platform;

namespace ClipLine.Shell;

/// <summary>读真实鼠标位置（物理像素）。交互模式的帧驱动用。</summary>
public static class CursorReader
{
    public static (int X, int Y) Position()
    {
        ShellNative.GetCursorPos(out ShellNative.POINT pt);
        return (pt.X, pt.Y);
    }
}

/// <summary>
/// 端到端冒烟：真实窗口 + 真实托盘 + 假时钟，经唯一应用门面接缝驱动。
/// 由 ClipLine.App --smoke 在 WinUI 3 启动后调用，验证整条产品启动路径。
/// </summary>
public static class ShellSmoke
{
    private static readonly List<(string Name, bool Pass, string Detail)> Checks = new();
    private static readonly string ProgressPath =
        Path.Combine(Path.GetTempPath(), "clipline-smoke", "progress.log");

    internal static void Progress(string step)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath)!);
            File.AppendAllText(ProgressPath, step + Environment.NewLine);
        }
        catch { /* 诊断不阻断冒烟 */ }
    }

    private static void Check(string name, bool pass, string detail = "") => Checks.Add((name, pass, detail));

    public static int Run()
    {
        Progress("run entered");
        // WinExe 无控制台：附加到父进程控制台；同时写结果文件兜底。
        ShellNative.AttachConsole(ShellNative.AttachParentProcess);
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { /* 无父控制台时走结果文件 */ }

        string tempRoot = Path.Combine(Path.GetTempPath(), "clipline-smoke");
        string resultPath = Path.Combine(tempRoot, "smoke-result.txt");

        try
        {
            Progress("run checks entered");
            RunChecks(tempRoot);
            Progress("run checks returned");
        }
        catch (Exception ex)
        {
            Progress("run checks threw: " + ex);
            Check("冒烟运行无异常", false, ex.GetType().Name + ": " + ex.Message);
        }

        var report = new StringBuilder();
        foreach ((string name, bool pass, string detail) in Checks)
            report.AppendLine($"  [{(pass ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
        int passed = 0;
        foreach ((_, bool pass, _) in Checks) if (pass) passed++;
        report.AppendLine();
        report.AppendLine($"VERDICT: {(passed == Checks.Count ? "PASS" : "FAIL")} ({passed}/{Checks.Count})");

        string text = report.ToString();
        Console.WriteLine(text);
        try { File.WriteAllText(resultPath, text); } catch { /* 结果文件只是兜底 */ }
        return passed == Checks.Count ? 0 : 1;
    }

    private static void RunChecks(string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        string ownDir = Path.Combine(tempRoot, "Screenshots");
        string picturesDir = Path.Combine(tempRoot, "Pictures");
        // 冒烟状态必须每次从零开始：否则上一轮把卡片「固定」后写回 state.json，
        // 下一轮就找不到未固定项，V-09 断言会假失败（真实踩到过 33/34）。
        string statePath = Path.Combine(tempRoot, "state.json");
        try { File.Delete(statePath); } catch { /* 只读介质时退化为沿用 */ }

        int awareness = ShellNative.GetAwarenessFromDpiAwarenessContext(ShellNative.GetThreadDpiAwarenessContext());
        Check("进程级 DPI 感知 (PMv2)", awareness == 2, $"awareness={awareness}, systemDpi={ShellNative.GetDpiForSystem()}");

        var clock = new FakeClock();
        var env = new ShellEnvironment(ShellNative.GetDpiForSystem() / 96.0, ownDir, picturesDir);
        var host = new ShellAppHost(clock, env, statePath);
        host.Start();
        Check("托盘图标（E 双线，message-only 宿主）", host.TrayAdded, $"host=0x{host.Tray.HostHandle.ToInt64():X}");

        // 彩色测试图：让导出的渲染帧能直观证明真实缩略图解码链路。
        string demoPath = Path.Combine(ownDir, "demo.png");
        GdiPlus.SaveBgraAsPng(ColorfulDemo(240, 160), 240, 160, demoPath);

        clock.NowMs = 0;
        host.Facade.Handle(new NewItem(demoPath));
        host.RenderFrame(); // 帧驱动重绘：新图到来后按最新状态快照刷新卡片
        Check("新截图 peek → PresentWindow → 绳窗口可见", host.Rope.IsVisible,
            $"hwnd=0x{host.Rope.Handle.ToInt64():X}, revealed={host.Facade.Current.IsRevealed}");

        long exStyle = ShellNative.GetWindowLongPtrW(host.Rope.Handle, ShellNative.GWL_EXSTYLE).ToInt64();
        Check("绳窗口样式 LAYERED|NOACTIVATE|TOOLWINDOW 且初始穿透",
            (exStyle & ShellNative.WS_EX_LAYERED) != 0
            && (exStyle & ShellNative.WS_EX_NOACTIVATE) != 0
            && (exStyle & ShellNative.WS_EX_TOOLWINDOW) != 0
            && (exStyle & ShellNative.WS_EX_APPWINDOW) == 0,
            $"exStyle=0x{exStyle:X}");

        Check("卡片占位已按状态渲染", host.Rope.CardRects.Count >= 1, $"{host.Rope.CardRects.Count} 张");

        string framePath = Path.Combine(tempRoot, "frame.png");
        bool dumped = host.Rope.DumpFrame(framePath);
        long frameBytes = File.Exists(framePath) ? new FileInfo(framePath).Length : 0;
        Check("渲染帧可导出为 PNG（真实缩略图证据）", dumped && frameBytes > 1000,
            $"{framePath} ({frameBytes} B)");

        if (host.Rope.CardRects.Count > 0)
        {
            (int cx, int cy, int cw, int ch) = host.Rope.CardRects[0];
            int midX = cx + cw / 2, midY = cy + ch / 2;

            clock.NowMs = 100;
            host.Tick(midX, midY);
            Check("卡片命中时清除穿透", !host.Rope.IsPassthrough, $"命中点 ({midX},{midY})");
            IntPtr hit = ShellNative.WindowFromPoint(new ShellNative.POINT(midX, midY));
            Check("卡片区 WindowFromPoint 命中绳窗口", hit == host.Rope.Handle,
                $"返回 0x{hit.ToInt64():X}");

            clock.NowMs = 200;
            host.Tick(10, 5);
            Check("空白区恢复穿透", host.Rope.IsPassthrough, "");
            IntPtr blank = ShellNative.WindowFromPoint(new ShellNative.POINT(10, 5));
            Check("空白区穿透到下层", blank != host.Rope.Handle, $"返回 0x{blank.ToInt64():X}");

            // ①c P0-6：托盘/热键唤出后绳子可聚焦，焦点框必须真的改变画面，
            // 失焦后恢复 NOACTIVATE 并撤掉焦点框（键盘可达不是纸面承诺）。
            string focusBefore = Path.Combine(tempRoot, "rope-focus-before.png");
            string focusAfter = Path.Combine(tempRoot, "rope-focus-after.png");
            host.Rope.DumpFrame(focusBefore);
            host.Rope.FocusForKeyboard();
            bool focused = host.Rope.KeyboardFocused && host.Rope.FocusIndex == 0;
            host.Rope.DumpFrame(focusAfter);
            // 同一帧的 1:1 局部放大：焦点框与夹子的细节证据（供人工目检）。
            host.Rope.DumpFrame(Path.Combine(tempRoot, "focus-zoom.png"), 800, 0, 320, 170);
            bool ringDrawn = File.Exists(focusBefore) && File.Exists(focusAfter)
                && !File.ReadAllBytes(focusBefore).SequenceEqual(File.ReadAllBytes(focusAfter));
            host.Rope.ReleaseKeyboard();
            bool released = !host.Rope.KeyboardFocused && host.Rope.FocusIndex == -1;
            Check("键盘可达：可聚焦、焦点框可见、失焦复原", focused && ringDrawn && released,
                $"焦点={focused} 焦点框={ringDrawn} 释放={released}");

            // ①d P0-5：拖放载荷必须真的是资源管理器认的 CF_HDROP（UTF-16 + 双零结尾），
            // 且「文件还在不在」要压过目标回报的 DROPEFFECT —— 这是卡片去留的唯一判据。
            string payload = Path.Combine(tempRoot, "拖放样本.png");
            File.WriteAllBytes(payload, Array.Empty<byte>());
            var (wide, doubleNull, decoded) = DragDropBridge.InspectPayload(payload);
            bool dropPayload = wide && doubleNull
                && string.Equals(decoded, payload, StringComparison.Ordinal);
            bool moved = DragDropBridge.Classify(2, false) == DragOutcome.Moved;
            bool liedMove = DragDropBridge.Classify(2, true) == DragOutcome.Copied;
            bool liedCopy = DragDropBridge.Classify(1, false) == DragOutcome.Moved;
            bool cancelled = DragDropBridge.Classify(0, true) == DragOutcome.Cancelled;
            Check("拖放：CF_HDROP 载荷正确、去留按磁盘判定",
                dropPayload && moved && liedMove && liedCopy && cancelled,
                $"宽字符={wide} 双零={doubleNull} 路径一致={decoded == payload} " +
                $"移走={moved} 谎报MOVE={liedMove} 谎报COPY={liedCopy} 取消={cancelled}");
        }

        // 指针离开 → peek(2500) 过期 → 欢迎窗 pin(5000) 解除 → 收起 → 0.3 s 后销毁。
        // 注意首次安装会触发 ShowWelcome 并 pin 住绳子，假时钟必须推进过 WelcomePinMs。
        clock.NowMs = 300;
        host.Tick(10, 600);
        clock.NowMs = 5400; // > WelcomePinMs(5000)，且 peek 与 retract 均已过期
        host.Tick(10, 600);
        Check("收起后门面隐藏绳窗口", !host.Rope.IsVisible, $"revealed={host.Facade.Current.IsRevealed}");

        clock.NowMs = 5800; // + DestroyDelay 300
        host.Tick(10, 600);
        Check("0.3 s 后销毁绳窗口", !host.Rope.Exists, "");

        bool leftover = false;
        ShellNative.EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256);
            ShellNative.GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() == RopeWindow.ClassName) leftover = true;
            return true;
        }, IntPtr.Zero);
        Check("EnumWindows 无残留绳窗口", !leftover, "");
        Check("托盘与瞬时线窗口解耦", host.TrayAdded && host.Tray.HostHandle != IntPtr.Zero, "");
        Progress("before menu selftest");
        (bool menuPass, string menuDetail) = RealInputBridge.SelfTest();
        Progress("after menu selftest");
        Check("托盘 16 项 Win32 菜单结构自检", menuPass, menuDetail);
        Check("平台命令接收器无异常", host.SinkErrors.Count == 0,
            host.SinkErrors.Count == 0 ? "" : string.Join("；", host.SinkErrors));
        Progress("checks done, disposing host");
        host.Dispose();
        Progress("host disposed");

        RunRegressionChecks(tempRoot);
        Progress("regression checks done");
        RunMotionChecks(tempRoot);
        Progress("motion checks done");
    }

    /// <summary>
    /// v1.1 动效：空绳热区唤出、入场摆入、取下掉落层。
    /// 独立 host + 假时钟，不干扰前面的启动与回归时序。
    /// </summary>
    private static void RunMotionChecks(string tempRoot)
    {
        string ownDir = Path.Combine(tempRoot, "Screenshots");
        string picturesDir = Path.Combine(tempRoot, "Pictures");
        var clock = new FakeClock { NowMs = 30_000 };
        var env = new ShellEnvironment(ShellNative.GetDpiForSystem() / 96.0, ownDir, picturesDir);
        var host = new ShellAppHost(clock, env, Path.Combine(tempRoot, "state-motion.json"));
        try
        {
            host.Start();
            // 先越过首次安装的 5 s pin，让空绳真正收起并销毁。
            host.Facade.Handle(new PointerMoved(10, 600));
            clock.NowMs += 5_400;
            host.Facade.AdvanceTo(clock.NowMs);
            clock.NowMs += 400;
            host.Facade.AdvanceTo(clock.NowMs);
            host.RenderFrame();
            bool hiddenAtStart = !host.Rope.IsVisible;

            // ① 空绳：指针停在顶部中间 1/3 区域 0.25 s 后必须把绳子放下来。
            host.Facade.Handle(new PointerMoved(640, 4));
            clock.NowMs += 250;
            host.Facade.AdvanceTo(clock.NowMs);
            host.RenderFrame();
            bool revealed = host.Facade.Current.IsRevealed && host.Rope.IsVisible;
            Check("空绳顶部热区唤出绳子", hiddenAtStart && revealed,
                $"收起={hiddenAtStart} 唤出={revealed}");

            // ② 入场摆动：新图带 16° 摆入与 -46 DIP 落下，2.5 s 后只剩静态倾角。
            string motionCard = Path.Combine(ownDir, "motion-card.png");
            GdiPlus.SaveBgraAsPng(ColorfulDemo(96, 64), 96, 64, motionCard);
            host.Facade.Handle(new NewItem(motionCard));
            host.RenderFrame();
            CardVisual arrive = host.CurrentView().Cards[0];

            // ④ 抗锯齿：摆动帧的卡片边缘必须是连续的多级 alpha 过渡。
            // 旧的逐行整数位移 + dx²+dy²≤r² 二值判定只会产生 0/255 硬边阶梯。
            var rect = host.Rope.CardRects.Count > 0
                ? host.Rope.CardRects[0] : (X: 0, Y: 0, W: 0, H: 0);
            var swungEdge = host.Rope.AlphaStats(rect.X - 24, rect.Y - 24,
                rect.X + rect.W + 24, rect.Y + rect.H + 24);
            Check("摆动边缘抗锯齿（多级 alpha 过渡，非二值阶梯）",
                swungEdge.Count >= 200 && swungEdge.Levels >= 8,
                $"半透明像素={swungEdge.Count} alpha级数={swungEdge.Levels}");

            // 抗锯齿不能把帧驱动拖垮：30 帧真实 Present（含 UpdateLayeredWindow）均值需 < 33 ms。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) host.Rope.Present(host.CurrentView());
            sw.Stop();
            double perFrame = sw.Elapsed.TotalMilliseconds / 30;
            Check("摆动帧双线性重采样在 30 fps 预算内", perFrame < 33,
                $"平均 {perFrame:F2} ms/帧（30 帧）");

            clock.NowMs += 2_500;
            host.RenderFrame();
            CardVisual rest = host.CurrentView().Cards[0];
            var restEdge = host.Rope.AlphaStats(rect.X - 24, rect.Y - 24,
                rect.X + rect.W + 24, rect.Y + rect.H + 24);
            Check("静止帧圆角仍有 1px 羽化（解析式 SDF 覆盖率）",
                restEdge.Count >= 100 && restEdge.Levels >= 8,
                $"半透明像素={restEdge.Count} alpha级数={restEdge.Levels}");
            bool swung = Math.Abs(arrive.SwingDegrees) > 10
                && Math.Abs(arrive.DropDip) > 10
                && Math.Abs(rest.SwingDegrees) < 6
                && Math.Abs(rest.DropDip) < 0.5;
            Check("入场 16° 摆入 + 46 DIP 落下并收敛", swung,
                $"起始角={arrive.SwingDegrees:F2}° 落下={arrive.DropDip:F1} → " +
                $"静止角={rest.SwingDegrees:F2}° 落下={rest.DropDip:F1}");

            // ③ 取下：StartFall 交给全屏掉落层，0.55 s 后自动隐藏。
            host.Facade.Handle(new FileDisappeared(motionCard));
            host.RenderFrame();
            bool fallVisible = host.FallWindowVisible;
            string fallPath = Path.Combine(tempRoot, "frame-fall.png");
            bool fallDumped = host.DumpFallFrame(fallPath);
            long fallBytes = File.Exists(fallPath) ? new FileInfo(fallPath).Length : 0;
            clock.NowMs += 600;
            host.RenderFrame();
            bool fallHidden = !host.FallWindowVisible;
            Check("单张取下 → 全屏掉落层出现并在 0.55 s 后收起",
                fallVisible && fallDumped && fallBytes > 500 && fallHidden,
                $"可见={fallVisible} 帧={fallBytes} B 收起={fallHidden}");
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// 用户实测反馈的三项回归：左键复制 + 「已复制」角标、监听目录删除联动、
    /// 悬停时的系统等待光标。用独立 host / 假时钟，不干扰上面的启动时序。
    /// </summary>
    private static void RunRegressionChecks(string tempRoot)
    {
        string ownDir = Path.Combine(tempRoot, "Screenshots");
        string picturesDir = Path.Combine(tempRoot, "Pictures");
        var clock = new FakeClock { NowMs = 10_000 };
        var env = new ShellEnvironment(ShellNative.GetDpiForSystem() / 96.0, ownDir, picturesDir);
        // 和 RunChecks 同理：上一轮「固定」会写回这里，不清空会让 V-09 断言假失败。
        string regressionState = Path.Combine(tempRoot, "state-regression.json");
        try { File.Delete(regressionState); } catch { /* 只读介质时退化为沿用 */ }
        var host = new ShellAppHost(clock, env, regressionState);
        try
        {
            host.Start();
            string cardPath = Path.Combine(ownDir, "regression-card.png");
            GdiPlus.SaveBgraAsPng(ColorfulDemo(64, 48), 64, 48, cardPath);
            AppResult added = host.Facade.Handle(new NewItem(cardPath));
            host.RenderFrame();
            Guid id = added.State.Items[^1].Id;

            string beforePath = Path.Combine(tempRoot, "frame-before-copy.png");
            host.Rope.DumpFrame(beforePath);

            // 首次运行可能已经弹过欢迎 / 捕获开关提示，先收起建立干净基线，
            // 才能准确断言「复制这个动作本身不会再次弹通知」。
            host.Tray.Notice?.Hide();
            bool noticeQuietBefore = host.Tray.Notice is null || !host.Tray.Notice.IsVisible;

            // ① 左键单击：用 SendMessageW 真的走一遍「WM → WndProc → 输入桥 → 门面」。
            // 临时把同步上下文置空，让 WndProc 的 Post 在冒烟线程里同步派发
            // （冒烟在 OnLaunched 里同步执行，消息泵尚未启动）。
            host.ConfigureAudit(true, tempRoot);
            var bridge = new RealInputBridge(host);
            host.Rope.Mouse += bridge.OnRopeMouse;
            (int rx, int ry, int rw, int rh) = host.Rope.CardRects[0];
            int clickX = rx + rw / 2, clickY = ry + rh / 2;
            IntPtr packed = new((clickY << 16) | (clickX & 0xFFFF));
            SynchronizationContext? savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                ShellNative.SendMessageW(host.Rope.Handle, ShellNative.WM_LBUTTONDOWN, UIntPtr.Zero, packed);
                ShellNative.SendMessageW(host.Rope.Handle, ShellNative.WM_LBUTTONUP, UIntPtr.Zero, packed);
            }
            finally { SynchronizationContext.SetSynchronizationContext(savedContext); }
            clock.NowMs += (long)env.SystemDoubleClickIntervalMs + 100;
            AppResult clicked = host.Facade.AdvanceTo(clock.NowMs);
            bool wroteClipboard = clicked.Commands.Any(c => c.Kind == CommandKind.WriteClipboardImage);
            bool badgeCommand = clicked.Commands.Any(c => c.Kind == CommandKind.ShowCopiedBadge);
            bool stateBadge = host.Facade.Current.Items.Any(i => i.Id == id && i.CopiedBadge);
            bool frameBadge = host.CurrentView().Cards.Any(c => c.Id == id && c.Copied);
            host.RenderFrame();
            string afterPath = Path.Combine(tempRoot, "frame-after-copy.png");
            host.Rope.DumpFrame(afterPath);
            byte[] beforeBytes = File.Exists(beforePath) ? File.ReadAllBytes(beforePath) : Array.Empty<byte>();
            byte[] afterBytes = File.Exists(afterPath) ? File.ReadAllBytes(afterPath) : Array.Empty<byte>();
            bool drawn = beforeBytes.Length > 1000 && afterBytes.Length > 1000
                && !beforeBytes.AsSpan().SequenceEqual(afterBytes);
            Check("左键单击 → 写剪贴板 + 卡片「已复制」角标",
                wroteClipboard && badgeCommand && stateBadge && frameBadge && drawn,
                $"copy={wroteClipboard} badge={badgeCommand} state={stateBadge} " +
                $"frame={frameBadge} 重绘={drawn} ({beforeBytes.Length}→{afterBytes.Length} B)");
            bool noticeQuietAfter = host.Tray.Notice is null || !host.Tray.Notice.IsVisible;
            Check("复制只保留卡片角标，不再弹出冗余通知窗",
                badgeCommand && noticeQuietBefore && noticeQuietAfter,
                $"角标命令={badgeCommand} 复制前静默={noticeQuietBefore} " +
                $"复制后静默={noticeQuietAfter}");
            host.ConfigureAudit(false, tempRoot);
            host.ResetAudit();

            // ①b 复制实现本身必须能把图片真的放进系统剪贴板（不只是发命令）。
            // 冒烟为测试会话，会短暂占用剪贴板；剪贴板被别的进程独占时重试。
            bool clipboardWritten = false;
            for (int attempt = 0; attempt < 3 && !clipboardWritten; attempt++)
            {
                clipboardWritten = GdiPlus.CopyFileToClipboard(cardPath);
                if (!clipboardWritten) Thread.Sleep(60);
            }
            bool hasBitmap = false, hasDrop = false;
            string? dropPath = null;
            if (clipboardWritten && ShellNative.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    hasBitmap = ShellNative.GetClipboardData(ShellNative.CF_BITMAP) != IntPtr.Zero;
                    IntPtr hDrop = ShellNative.GetClipboardData(ShellNative.CF_HDROP);
                    hasDrop = hDrop != IntPtr.Zero;
                    if (hasDrop)
                    {
                        IntPtr p = ShellNative.GlobalLock(hDrop);
                        if (p != IntPtr.Zero)
                        {
                            try
                            {
                                dropPath = System.Runtime.InteropServices.Marshal
                                    .PtrToStringUni(p + 20); // DROPFILES 头占 20 字节
                            }
                            finally { ShellNative.GlobalUnlock(hDrop); }
                        }
                    }
                }
                finally { ShellNative.CloseClipboard(); }
            }
            Check("复制实现真写入系统剪贴板 (CF_BITMAP)", clipboardWritten && hasBitmap,
                $"写入={clipboardWritten} 可读回位图={hasBitmap}");
            bool dropOk = hasDrop && dropPath is not null &&
                dropPath.EndsWith(Path.GetFileName(cardPath), StringComparison.OrdinalIgnoreCase);
            Check("复制同时写入 CF_HDROP（资源管理器 Ctrl+V 可粘贴文件）",
                clipboardWritten && dropOk,
                $"可读回文件列表={hasDrop} 路径={dropPath ?? "null"}");

            // ② 监听目录里的图片被删除 / 改名，绳子必须联动取下。
            string watched = Path.Combine(tempRoot, "Watched");
            Directory.CreateDirectory(watched);
            string watchedFile = Path.Combine(watched, "watched.png");
            GdiPlus.SaveBgraAsPng(ColorfulDemo(48, 32), 48, 32, watchedFile);
            host.Facade.Handle(new NewItem(watchedFile, InOwnDirectory: false));
            bool onLineBefore = host.Facade.Current.Items.Any(i => i.Path == watchedFile);
            host.Facade.Handle(new FileDisappeared(watchedFile));
            bool onLineAfter = host.Facade.Current.Items.Any(i => i.Path == watchedFile);
            bool mapped = FolderWatcher.ToChange(2) == FolderChange.Removed
                && FolderWatcher.ToChange(4) == FolderChange.RenamedOldName
                && FolderWatcher.IsRemoval(FolderChange.Removed)
                && FolderWatcher.IsRemoval(FolderChange.RenamedOldName);
            Check("监听目录删除/改名 → 绳子联动取下",
                onLineBefore && !onLineAfter && mapped,
                $"删除前在绳上={onLineBefore} 删除后仍在={onLineAfter} 事件映射={mapped}");

            // ③ 窗口类与 WM_SETCURSOR 都显式给出普通光标，不能沿用等待/启动光标。
            IntPtr arrow = RopeWindow.ClassCursor;
            IntPtr hand = RopeWindow.CursorFor(true);
            IntPtr wait = ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcWait);
            IntPtr starting = ShellNative.LoadCursorW(IntPtr.Zero, ShellNative.IdcAppStarting);
            bool cursorOk = arrow != IntPtr.Zero && hand != IntPtr.Zero
                && arrow != wait && arrow != starting && hand != wait && hand != starting;
            IntPtr classCursor = host.Rope.Exists
                ? ShellNative.GetClassLongPtrW(host.Rope.Handle, ShellNative.GCLP_HCURSOR)
                : IntPtr.Zero;
            Check("绳窗口显式重置光标（无加载小圆圈）",
                cursorOk && classCursor != IntPtr.Zero && classCursor != wait && classCursor != starting,
                $"arrow=0x{arrow.ToInt64():X} hand=0x{hand.ToInt64():X} " +
                $"classCursor=0x{classCursor.ToInt64():X}");

            // ④ 自绘轻量通知（取代系统 NIF_INFO 气泡）：只服务欢迎 / 捕获开关 /
            // 快捷键说明这类需要阅读的场景，复制场景已移除（见上面断言）。
            // 尺寸缩 20%、可导出帧，用相对时间确定性推进淡入 → 停留 → 淡出 → 收起。
            const string noticeTitle = "欢迎使用 ClipLine";
            const string noticeText = "截个图，它就会挂在这里。右键托盘图标可打开设置菜单。";
            host.Tray.ShowBalloon(noticeTitle, noticeText);
            NoticeWindow? notice = host.Tray.Notice;
            string noticePath = Path.Combine(tempRoot, "frame-notice.png");
            bool noticeDumped = notice?.DumpFrame(noticePath) ?? false;
            long noticeBytes = File.Exists(noticePath) ? new FileInfo(noticePath).Length : 0;
            long n0 = Environment.TickCount64;
            bool noticeShown = notice is not null && notice.Show(noticeTitle, noticeText, n0);
            notice?.Tick(n0 + 200);
            bool fadeIn = notice?.LastAlpha == 255;
            // P1-13：停留从 3.2 s 提到 6 s（够读完两行说明），淡出仍在 250 ms 内完成。
            notice?.Tick(n0 + 6150);
            bool fadeOut = notice is not null && notice.LastAlpha < 255;
            notice?.Tick(n0 + 6300);
            bool noticeHidden = notice is not null && !notice.IsVisible;
            int expectedW = (int)Math.Round(NoticeWindow.WidthDip * (notice?.Scale ?? 1));
            int expectedH = (int)Math.Round(NoticeWindow.HeightDip * (notice?.Scale ?? 1));
            bool sized = notice is not null && notice.WidthPx == expectedW
                && notice.HeightPx == expectedH
                && Math.Abs(NoticeWindow.SizeScale - 0.8) < 0.001;
            Check("自绘通知窗替代系统气泡（288×76 DIP，缩 20%）",
                noticeShown && noticeDumped && noticeBytes > 1000 && sized,
                $"尺寸={notice?.WidthPx}×{notice?.HeightPx}px 缩放={notice?.Scale} 帧={noticeBytes} B");
            Check("通知淡入 / 淡出 / 自动收起（确定性时序）",
                fadeIn && fadeOut && noticeHidden,
                $"淡入={fadeIn} 淡出={fadeOut} 收起={noticeHidden}");

            // ⑤ 通知卡贴工作区右下角：UpdateLayeredWindow 的 pptDst 会重置窗口位置，
            // 必须把 Position() 算出的坐标传进去，否则通知会跳到屏幕左上角。
            long n1 = Environment.TickCount64;
            notice?.Show(noticeTitle, noticeText, n1);
            notice?.Tick(n1 + 200);
            bool noticePlaced = false;
            string noticeRect = "无窗口矩形";
            if (notice is not null && ShellNative.GetWindowRect(notice.Handle, out ShellNative.RECT nr))
            {
                int sw = ShellNative.GetSystemMetrics(ShellNative.SM_CXSCREEN);
                int sh = ShellNative.GetSystemMetrics(ShellNative.SM_CYSCREEN);
                noticeRect = $"({nr.Left},{nr.Top})-({nr.Right},{nr.Bottom}) / 屏幕 {sw}×{sh}";
                noticePlaced = nr.Right > sw / 2 && nr.Top > sh / 2 && nr.Right <= sw;
            }
            Check("自绘通知卡停在右下角（分层窗口目标点未把它拉回左上角）",
                noticePlaced, noticeRect);
            notice?.Hide();

            // ⑥ v1.4：四个系统替身窗口各自独立，取代统一降级到系统默认应用。
            string toolImage = Path.Combine(tempRoot, "tool-demo.png");
            GdiPlus.SaveBgraAsPng(ColorfulDemo(64, 48), 64, 48, toolImage);
            var toolPaths = new List<string> { toolImage };
            ToolMode[] modes = { ToolMode.QuickLook, ToolMode.Markup, ToolMode.Trim, ToolMode.Held };
            var shown = new bool[modes.Length];
            var dumped = new bool[modes.Length];
            for (int i = 0; i < modes.Length; i++)
            {
                shown[i] = host.Tools.Show(modes[i], toolPaths, 0, Guid.Empty);
                string frame = Path.Combine(tempRoot, "frame-tool-" + modes[i] + ".png");
                dumped[i] = host.Tools.DumpFrame(modes[i], frame)
                    && File.Exists(frame) && new FileInfo(frame).Length > 1000;
                host.Tools.Close(modes[i]);
            }
            bool allWindows = shown.All(v => v) && dumped.All(v => v);
            bool distinctSizes = modes.Select(ToolWindow.SizeFor).Distinct().Count() == modes.Length;
            Check("四个替身窗口各自独立且尺寸不同（快速查看/标注/修剪/拿在手上）",
                allWindows && distinctSizes,
                $"显示={string.Join(",", shown)} 帧={string.Join(",", dumped)} " +
                $"尺寸={string.Join(" / ", modes.Select(m => ToolWindow.SizeFor(m).ToString()))}");
            host.Tools.CloseAll();

            // ⑦ V-09 绳结：固定后绳帧必须发生可见变化，并能导出局部放大证据。
            string ropeBefore = Path.Combine(tempRoot, "rope-before-pin.png");
            string ropeAfter = Path.Combine(tempRoot, "rope-after-pin.png");
            host.Rope.DumpFrame(ropeBefore);
            ItemSnapshot? pinTarget = host.Facade.Current.Items.FirstOrDefault(i => !i.Kept);
            if (pinTarget is not null)
                host.Facade.Handle(new ContextMenuItem(pinTarget.Id,
                    ClipLine.Domain.ContextAction.Pin));
            host.RenderFrame();
            host.Rope.DumpFrame(ropeAfter);
            bool ropeChanged = File.Exists(ropeBefore) && File.Exists(ropeAfter)
                && !File.ReadAllBytes(ropeBefore).SequenceEqual(File.ReadAllBytes(ropeAfter));
            string knotPath = Path.Combine(tempRoot, "frame-knot-zoom.png");
            bool knotDumped = host.Rope.DumpFrame(knotPath, 560, 0, 240, 90)
                && File.Exists(knotPath) && new FileInfo(knotPath).Length > 200;
            Check("V-09 固定后绳帧出现绳结 / 靛蓝分区且局部证据可导出",
                pinTarget is not null && ropeChanged && knotDumped,
                $"固定项={pinTarget is not null} 帧变化={ropeChanged} 局部帧={knotDumped} " +
                $"条目数={host.Facade.Current.Items.Count} " +
                $"已固定={host.Facade.Current.Items.Count(i => i.Kept)}");

            // ⑧ 像素级回归：染色段必须是靛蓝（B>R），不能因 B/G/R 色序反转成粉红；
            // 且亚像素 Stroke 让带宽连续，不能像整数行染色那样出现整像素台阶。
            // 这两类缺陷都曾被真实证据帧抓到，仅断言「帧内容不同」是拦不住的。
            int blueSamples = 0, totalSamples = 0, maxBandJump = 0, maxBandMass = 0;
            int prevMass = -1;
            var pinnedRects = host.Rope.CardRects;
            int tintFrom = (int)Math.Round(24 * host.Scale) + 10;
            int tintTo = pinnedRects.Count >= 2
                ? (pinnedRects[0].X + pinnedRects[0].W + pinnedRects[1].X) / 2 - 16
                : tintFrom + 120;
            double widthDip = env.ScreenWidthDip;
            for (int x = tintFrom; x < tintTo; x++)
            {
                double y = Layout.RopeY(x / host.Scale, widthDip) * host.Scale;
                int mass = 0, bestA = 0, bestR = 0, bestB = 0;
                for (int yy = (int)Math.Floor(y) - 3; yy <= (int)Math.Ceiling(y) + 4; yy++)
                {
                    (int r, _, int b, int a) = host.Rope.PixelAt(x, yy);
                    mass += a;
                    if (a > bestA) { bestA = a; bestR = r; bestB = b; }
                }
                if (bestA < 8) continue;
                totalSamples++;
                if (bestB > bestR + 12) blueSamples++;
                if (prevMass >= 0) maxBandJump = Math.Max(maxBandJump, Math.Abs(mass - prevMass));
                prevMass = mass;
                maxBandMass = Math.Max(maxBandMass, mass);
            }
            bool tintBlue = totalSamples > 40 && blueSamples >= totalSamples * 9 / 10;
            bool bandSmooth = totalSamples > 40 && maxBandJump > 0
                && maxBandJump <= Math.Max(60, maxBandMass / 4);
            Check("固定绳段为靛蓝（非粉红）且亚像素连续（真实像素采样）",
                tintBlue && bandSmooth,
                $"采样={totalSamples} 蓝色占优={blueSamples} 最大相邻带宽跳变={maxBandJump} " +
                $"最大带宽={maxBandMass} x=[{tintFrom},{tintTo})");
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>左橙 / 右上蓝 / 右下白的可辨识 BGRA 测试图。</summary>
    private static byte[] ColorfulDemo(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool left = x < width / 2;
                bool upper = y < height / 2;
                int r = left ? 239 : upper ? 108 : 255;
                int g = left ? 138 : upper ? 99 : 255;
                int b = left ? 36 : upper ? 244 : 255;
                int i = (y * width + x) * 4;
                pixels[i] = (byte)b;
                pixels[i + 1] = (byte)g;
                pixels[i + 2] = (byte)r;
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>1×1 透明 PNG，含完整 IEND。</summary>
    private static byte[] MinimalPng() => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54,
        0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00, 0x05,
        0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82,
    };
}
