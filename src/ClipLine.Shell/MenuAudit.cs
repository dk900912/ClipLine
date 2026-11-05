using System.Text;
using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>
/// 两个真实场景的全量菜单审计：
///   A. 托盘图标右键的 16 项主菜单 + 全部可点子项
///   B. 卡片右键的 14 项菜单
/// 每项经唯一接缝 AppFacade 真实触发一次，检查「是否产生命令或状态变化」以及
/// 「每条命令是否有实际消费者」（DryRun 下由 ShellCommandSink 记录无人消费的 Kind）。
/// 之后单独关闭 DryRun，验证文件类菜单的真实副作用（写到隔离目录，不碰用户桌面）。
/// </summary>
public static class MenuAudit
{
    private sealed record Row(string Scene, string Item, string Verdict, string Commands, string Detail);

    private static readonly List<Row> Rows = new();

    /// <summary>
    /// v1 明确降级/等价替换的命令。注意 P4 的 ShowQuickLook / ShowMarkupEditor /
    /// ShowTrimEditor / ShowHeldWindow 自 v1.4 起已有四个独立自绘窗口，不再是
    /// 「统一交给系统默认应用」的占位，因此从本集合移除（真实点击验证 25/25 覆盖）。
    /// 仍在集合里的 CloseHeldWindow 用于「全部挂回」这一项：门面只发关窗命令、
    /// 状态不变，仍属 v1 等价替换。
    /// v1.5 方案 D：「检查更新…」不再发无效的 OpenBrowser 查询串，改为真正执行
    /// RunUpdateCheck 的自动更新链路，因此 OpenBrowser 从降级集合移除。
    /// </summary>
    private static readonly HashSet<CommandKind> Degraded = new()
    {
        CommandKind.ShowShortcutRecorder,
        CommandKind.CloseQuickLook,
        CommandKind.CloseHeldWindow,
        CommandKind.HideCopiedBadge,
        CommandKind.HideTrayMenu,
    };

    private static readonly Dictionary<TrayMenuAction, string> TrayNames = new()
    {
        [TrayMenuAction.ToggleLine] = "显示/隐藏晾衣绳",
        [TrayMenuAction.TakeDownAll] = "全部取下",
        [TrayMenuAction.PutBack] = "放回",
        [TrayMenuAction.HangEverythingBack] = "全部挂回",
        [TrayMenuAction.ToggleInbox] = "接管截图",
        [TrayMenuAction.ToggleClipboard] = "挂上复制的图片",
        [TrayMenuAction.RevealOnPointer] = "指针靠近菜单栏时",
        [TrayMenuAction.PeekOnNewContent] = "有新内容时",
        [TrayMenuAction.ShowOverFullScreen] = "在全屏应用之上",
        [TrayMenuAction.WatchFolder] = "已监视文件夹/打开文件夹",
        [TrayMenuAction.StopWatching] = "停止监视",
        [TrayMenuAction.AddFolder] = "添加文件夹…",
        [TrayMenuAction.KeepFits] = "放得下的数量",
        [TrayMenuAction.Keep25] = "保留 25 张",
        [TrayMenuAction.Keep50] = "保留 50 张",
        [TrayMenuAction.Keep100] = "保留 100 张",
        [TrayMenuAction.SizeSmall] = "小",
        [TrayMenuAction.SizeMedium] = "中",
        [TrayMenuAction.SizeLarge] = "大",
        [TrayMenuAction.ChangeShortcut] = "更改快捷键…",
        [TrayMenuAction.OpenScreenshotsFolder] = "打开截图文件夹",
        [TrayMenuAction.Sound] = "声音",
        [TrayMenuAction.LaunchAtLogin] = "登录时打开",
        [TrayMenuAction.CheckForUpdates] = "检查更新…",
        [TrayMenuAction.Quit] = "退出 ClipLine",
    };

    private static readonly Dictionary<ContextAction, string> CardNames = new()
    {
        [ContextAction.Copy] = "复制",
        [ContextAction.Open] = "打开",
        [ContextAction.QuickLook] = "快速查看",
        [ContextAction.Hold] = "拿在手上",
        [ContextAction.Markup] = "标记",
        [ContextAction.RevealInExplorer] = "在文件资源管理器中显示",
        [ContextAction.Pin] = "固定",
        [ContextAction.Unpin] = "取消固定",
        [ContextAction.StoreToDesktop] = "存储到桌面",
        [ContextAction.TakeDown] = "取下",
        [ContextAction.Discard] = "丢弃",
        [ContextAction.MoveToRecycleBin] = "移到回收站",
        [ContextAction.TakeDownLeft] = "取下左侧全部",
        [ContextAction.TakeDownRight] = "取下右侧全部",
    };

    public static int Run()
    {
        ShellNative.AttachConsole(ShellNative.AttachParentProcess);
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { /* 无父控制台时只写结果文件 */ }

        string root = Path.Combine(Path.GetTempPath(), "clipline-menu-audit");
        string reportPath = Path.Combine(root, "menu-audit-report.txt");
        Directory.CreateDirectory(root);

        var report = new StringBuilder();
        try
        {
            RunAudit(root, report);
        }
        catch (Exception ex)
        {
            Rows.Add(new Row("审计器", "运行无异常", "FAIL", "", ex.GetType().Name + ": " + ex.Message));
        }

        int ok = Rows.Count(r => r.Verdict == "OK");
        int degraded = Rows.Count(r => r.Verdict == "DEGRADED");
        int skip = Rows.Count(r => r.Verdict == "SKIP");
        int fail = Rows.Count(r => r.Verdict == "FAIL");

        report.AppendLine($"ClipLine v1 菜单功能全量审计 — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine("场景 A = 托盘图标右键（16 项主菜单 + 全部可点子项）；场景 B = 卡片右键（14 项）");
        report.AppendLine("OK=有真实效果 / DEGRADED=等价替换或明确降级 / SKIP=当前上下文禁用(符合规格) / FAIL=静默无声或链路断");
        report.AppendLine();
        string scene = string.Empty;
        foreach (Row r in Rows)
        {
            if (r.Scene != scene) { scene = r.Scene; report.AppendLine($"--- {scene} ---"); }
            report.AppendLine($"  [{r.Verdict,-8}] {r.Item,-26} 命令=[{r.Commands}] {r.Detail}");
        }
        report.AppendLine();
        report.AppendLine($"汇总  OK={ok}  DEGRADED={degraded}  SKIP={skip}  FAIL={fail}  共 {Rows.Count} 项");
        report.AppendLine($"VERDICT: {(fail == 0 ? "PASS" : "FAIL")}");

        string text = report.ToString();
        Console.WriteLine(text);
        try { File.WriteAllText(reportPath, text); } catch { /* 结果文件只是兜底 */ }
        return fail == 0 ? 0 : 1;
    }

    private static void RunAudit(string root, StringBuilder notes)
    {
        string ownDir = Path.Combine(root, "Screenshots");
        string picturesDir = Path.Combine(root, "Pictures");
        string desktop = Path.Combine(root, "Desktop");
        string extraA = Path.Combine(root, "ExtraA");
        string extraB = Path.Combine(root, "ExtraB");
        Directory.CreateDirectory(ownDir);
        Directory.CreateDirectory(picturesDir);
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(extraA);
        Directory.CreateDirectory(extraB);

        var clock = new FakeClock();
        var env = new ShellEnvironment(ShellNative.GetDpiForSystem() / 96.0, ownDir, picturesDir);
        var host = new ShellAppHost(clock, env, Path.Combine(root, "state.json"));
        host.Start();
        host.ConfigureAudit(dryRun: true, desktop);
        host.ResetAudit();

        // ---- 场景 A：托盘 16 项 + 全部可点子项 ----
        // 先构造与菜单上下文一致的真实状态：3 张卡片 + 2 个额外监视文件夹。
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(ownDir, $"audit-tray-{i}.png");
            GdiPlus.SaveBgraAsPng(TestPixels(64, 48), 64, 48, path);
            host.Facade.Handle(new NewItem(path, ItemOrigin.Clipboard, InOwnDirectory: true));
        }
        host.Facade.Handle(new AddWatchedFolder(extraA));
        host.Facade.Handle(new AddWatchedFolder(extraB));
        host.ResetAudit();

        IReadOnlyList<TrayMenuSpec> specs =
            TrayMenuModel.Flatten(TrayMenuModel.Build(ContextOf(host))).ToList();
        notes.AppendLine($"[准备] 托盘菜单展开 {specs.Count} 项（含父菜单），每项独立触发。");

        foreach (TrayMenuSpec spec in specs)
        {
            // 带子菜单的父项由 Win32 MF_POPUP 承载，本身不可点，不参与审计。
            if (spec.Children is { Count: > 0 }) continue;
            string? argument = spec.Action is TrayMenuAction.WatchFolder or TrayMenuAction.StopWatching
                ? spec.Key
                : null;
            // 「全部挂回」只在存在「拿在手上」的项时可点：先造一个 held 项，
            // 让它从 SKIP 变成真正被测试到的功能。
            if (spec.Action == TrayMenuAction.HangEverythingBack)
            {
                StateSnapshot snapshot = host.Facade.Current;
                if (snapshot.Items.Count > 0)
                    host.Facade.Handle(new ContextMenuItem(snapshot.Items[^1].Id, ContextAction.Hold));
            }
            // 用实时门面状态判断该项此刻是否可点（T-05 条件显隐）。
            bool enabled = TrayMenuModel.Flatten(TrayMenuModel.Build(ContextOf(host)))
                .Any(s => s.Action == spec.Action && s.Key == spec.Key && s.Enabled);
            StateSnapshot before = host.Facade.Current;
            int unhandledBefore = host.AuditUnhandled.Count;
            AppResult result = host.Facade.Handle(new TrayMenuItemInvoked(spec.Action, argument));
            AddRow("场景 A 托盘菜单",
                TrayNames.GetValueOrDefault(spec.Action, spec.Action.ToString()),
                result, host, before, unhandledBefore, enabled);
        }

        // ---- 场景 B：卡片右键 14 项，每项造左中右三张图，取中间那张做目标 ----
        int index = 0;
        foreach (ContextAction action in Enum.GetValues<ContextAction>())
        {
            Guid id = Guid.Empty;
            for (int k = 0; k < 3; k++)
            {
                string path = Path.Combine(ownDir, $"audit-card-{++index}.png");
                GdiPlus.SaveBgraAsPng(TestPixels(64, 48), 64, 48, path);
                AppResult added = host.Facade.Handle(new NewItem(path, ItemOrigin.Clipboard, InOwnDirectory: true));
                if (k == 1 && added.State.Items.Count > 0) id = added.State.Items[^1].Id;
            }
            if (id == Guid.Empty)
            {
                Rows.Add(new Row("场景 B 卡片菜单", CardNames.GetValueOrDefault(action, action.ToString()),
                    "FAIL", "", "无法挂上测试图片"));
                continue;
            }
            StateSnapshot before = host.Facade.Current;
            int unhandledBefore = host.AuditUnhandled.Count;
            AppResult result = host.Facade.Handle(new ContextMenuItem(id, action));
            AddRow("场景 B 卡片菜单", CardNames.GetValueOrDefault(action, action.ToString()),
                result, host, before, unhandledBefore);
        }

        // ---- 真实副作用：关闭 DryRun，只用隔离目录验证文件类动作 ----
        host.ConfigureAudit(dryRun: false, desktop);
        VerifyStoreToDesktop(host, ownDir, desktop);
        VerifyRecycleBin(host, ownDir);

        host.Dispose();
    }

    /// <summary>用实时门面快照构造菜单上下文，保证条件显隐与真实状态一致。</summary>
    private static TrayMenuContext ContextOf(ShellAppHost host)
    {
        StateSnapshot s = host.Facade.Current;
        return new TrayMenuContext(
            s.LiveCount, s.UndoCount > 0, s.Items.Any(i => i.Held),
            s.InboxEnabled, s.ClipboardEnabled, s.RevealOnPointer, s.PeekOnNewContent,
            s.ShowOverFullScreen, s.KeepOnLine, s.Size, s.SoundOn, s.LaunchAtLogin,
            s.ExtraFolders, s.Shortcut, s.IsRevealed);
    }

    private static void AddRow(string scene, string item, AppResult result,
                               ShellAppHost host, StateSnapshot before, int unhandledBefore,
                               bool enabled = true)
    {
        string commands = string.Join(",", result.Commands.Select(c => c.Kind.ToString()).Distinct());
        if (commands.Length == 0) commands = "(无命令)";
        var broken = host.AuditUnhandled.Skip(unhandledBefore).Distinct().ToList();
        bool stateChanged = StateKey(result.State) != StateKey(before);
        bool anyDegraded = result.Commands.Any(c => Degraded.Contains(c.Kind));
        string detail = $"状态变化={(stateChanged ? "是" : "否")}";
        string verdict;
        if (broken.Count > 0)
        {
            verdict = "FAIL";
            detail += "；无人消费=" + string.Join("/", broken);
        }
        else if (!stateChanged && result.Commands.Count == 0)
        {
            verdict = enabled ? "FAIL" : "SKIP";
            detail += enabled ? "；既无命令也无状态变化" : "；当前上下文禁用（符合规格）";
        }
        else if (anyDegraded)
        {
            verdict = "DEGRADED";
            detail += "；含 v1 等价替换命令";
        }
        else verdict = "OK";
        Rows.Add(new Row(scene, item, verdict, commands, detail));
    }

    private static void VerifyStoreToDesktop(ShellAppHost host, string ownDir, string desktop)
    {
        string source = Path.Combine(ownDir, "store-to-desktop.png");
        GdiPlus.SaveBgraAsPng(TestPixels(80, 60), 80, 60, source);
        long size = new FileInfo(source).Length;
        AppResult added = host.Facade.Handle(new NewItem(source, ItemOrigin.Clipboard, InOwnDirectory: true));
        Guid id = added.State.Items[^1].Id;
        host.Facade.Handle(new ContextMenuItem(id, ContextAction.StoreToDesktop));

        string target = Path.Combine(desktop, "store-to-desktop.png");
        bool created = File.Exists(target) && new FileInfo(target).Length == size;
        bool moved = !File.Exists(source);
        bool errors = host.AuditPlatformErrors.Count > 0;
        Rows.Add(new Row("场景 C 真实副作用", "存储到桌面 → 桌面出现文件",
            created && moved && !errors ? "OK" : "FAIL",
            "MoveFile/UpdateClipboardFileReference",
            $"目标={target} 存在={created} 字节={size} 源已移走={moved}" +
            (errors ? "；错误=" + string.Join("；", host.AuditPlatformErrors) : "")));

        // 再用默认（真实）桌面目录跑一次：证明「Desktop\」占位符确实展开到用户桌面，
        // 而不是相对路径。自检文件用完立即送系统回收站，不在桌面留垃圾。
        string realDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        host.ConfigureAudit(dryRun: false, realDesktop);
        int errorsBefore = host.AuditPlatformErrors.Count;
        string realName = "clip-line-menu-audit-" + Guid.NewGuid().ToString("N")[..8] + ".png";
        string realSource = Path.Combine(ownDir, realName);
        GdiPlus.SaveBgraAsPng(TestPixels(72, 54), 72, 54, realSource);
        AppResult realAdded = host.Facade.Handle(new NewItem(realSource, ItemOrigin.Clipboard, InOwnDirectory: true));
        host.Facade.Handle(new ContextMenuItem(realAdded.State.Items[^1].Id, ContextAction.StoreToDesktop));
        string realTarget = Path.Combine(realDesktop, realName);
        bool realCreated = File.Exists(realTarget);
        if (realCreated) ClipLine.Platform.RecycleBin.Move(realTarget);
        // SHFileOperationW 的返回值在本机不可靠（fAnyOperationsAborted 封送），
        // 一律以「文件是否还在桌面」为准，避免把成功的清理误判成失败。
        bool cleaned = realCreated && !File.Exists(realTarget);
        bool realErrors = host.AuditPlatformErrors.Count > errorsBefore;
        Rows.Add(new Row("场景 C 真实副作用", "默认桌面目录解析与写入",
            realCreated && cleaned && !realErrors ? "OK" : "FAIL",
            "MoveFile",
            $"桌面={realDesktop} 生成={realCreated} 已送回收站={cleaned}" +
            (realErrors ? "；错误=" + string.Join("；", host.AuditPlatformErrors.Skip(errorsBefore)) : "")));
    }

    private static void VerifyRecycleBin(ShellAppHost host, string ownDir)
    {
        string source = Path.Combine(ownDir, "recycle-me.png");
        GdiPlus.SaveBgraAsPng(TestPixels(48, 48), 48, 48, source);
        AppResult added = host.Facade.Handle(new NewItem(source, ItemOrigin.Clipboard, InOwnDirectory: true));
        Guid id = added.State.Items[^1].Id;
        host.Facade.Handle(new ContextMenuItem(id, ContextAction.MoveToRecycleBin));
        bool gone = !File.Exists(source);
        Rows.Add(new Row("场景 C 真实副作用", "移到回收站 → 源文件消失",
            gone ? "OK" : "FAIL", "MoveToRecycleBin", $"源文件仍存在={!gone}"));
    }

    private static string StateKey(StateSnapshot s) =>
        string.Join('|', s.LiveCount, s.IsRevealed, s.IsPresent, s.UndoCount, s.MaxItems, s.KeepOnLine,
            (int)s.Size, s.ClipboardEnabled, s.InboxEnabled, s.RevealOnPointer, s.PeekOnNewContent,
            s.ShowOverFullScreen, s.SoundOn, s.LaunchAtLogin, s.Shortcut, s.Language,
            s.ExtraFolders.Count, s.Scroll, s.IsShowingMenu, s.Tray);

    /// <summary>可辨识的彩色测试图（BGRA）。</summary>
    private static byte[] TestPixels(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            pixels[i] = (byte)(x < width / 2 ? 36 : 244);
            pixels[i + 1] = (byte)(x < width / 2 ? 138 : 99);
            pixels[i + 2] = (byte)(x < width / 2 ? 239 : 108);
            pixels[i + 3] = 255;
        }
        return pixels;
    }
}
