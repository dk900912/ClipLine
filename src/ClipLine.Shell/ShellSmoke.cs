using System.Text;
using ClipLine.Application;

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

        int awareness = ShellNative.GetAwarenessFromDpiAwarenessContext(ShellNative.GetThreadDpiAwarenessContext());
        Check("进程级 DPI 感知 (PMv2)", awareness == 2, $"awareness={awareness}, systemDpi={ShellNative.GetDpiForSystem()}");

        var clock = new FakeClock();
        var env = new ShellEnvironment(ShellNative.GetDpiForSystem() / 96.0, ownDir, picturesDir);
        var host = new ShellAppHost(clock, env, Path.Combine(tempRoot, "state.json"));
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
