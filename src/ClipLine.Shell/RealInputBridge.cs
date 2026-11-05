using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClipLine.Application;
using ClipLine.Domain;
using ClipLine.Platform;

namespace ClipLine.Shell;

/// <summary>
/// v1 的真实输入桥：把 Win32 消息（剪贴板 / 目录 / 托盘 / 卡片鼠标 / 全局热键）
/// 全部翻译成应用门面的离散事件，再把门面发出的交互命令落到 Win32 菜单、
/// 系统默认应用、剪贴板与系统气泡。全程零轮询：剪贴板用 WM_CLIPBOARDUPDATE，
/// 目录用 ReadDirectoryChangesW，指针用既有 30 fps 帧驱动。
///
/// 唯一测试接缝仍是 AppFacade：本类不含任何业务判断，只做协议转换。
/// 冒烟模式不构造本类，128 项确定性用例因此不受真实剪贴板/文件系统影响。
/// </summary>
internal sealed class RealInputBridge : IDisposable
{
    private sealed record MenuEntry(int Id, TrayMenuAction Action, string? Argument);

    private readonly ShellAppHost _host;
    private readonly SynchronizationContext? _context;
    private readonly List<FolderWatcher> _watchers = new();
    private readonly Dictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MenuEntry> _menuEntries = new();
    private string? _lastClipboardHash;
    private Guid _contextItemId;
    private bool _clipboardAttached;
    private bool _hotKeyRegistered;
    private bool _disposed;

    private static readonly string DiagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipLine", "diagnostics.log");

    /// <summary>纯本地诊断日志（无网络、无统计）；只在离散事件时追加一行。</summary>
    private static void Diag(string message)
    {
        try
        {
            string? dir = Path.GetDirectoryName(DiagPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(DiagPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* 诊断失败绝不影响主链路 */ }
    }

    internal RealInputBridge(ShellAppHost host)
    {
        _host = host;
        _context = SynchronizationContext.Current;
    }

    internal void Attach()
    {
        IntPtr hwnd = _host.Tray.HostHandle;
        if (hwnd == IntPtr.Zero) return;

        _clipboardAttached = ShellNative.AddClipboardFormatListener(hwnd);
        uint modifiers = ShellNative.MOD_CONTROL | ShellNative.MOD_ALT;
        _hotKeyRegistered = ShellNative.RegisterHotKey(hwnd, 1, modifiers | ShellNative.MOD_NOREPEAT, 'T');
        if (!_hotKeyRegistered) // 部分输入法/远程桌面会拒绝 NOREPEAT，退回普通注册
            _hotKeyRegistered = ShellNative.RegisterHotKey(hwnd, 1, modifiers, 'T');

        _host.Tray.MenuRequested += ShowTrayMenu;
        _host.Tray.LeftClicked += ToggleLine;
        _host.Tray.ClipboardUpdated += OnClipboardUpdated;
        _host.Tray.HotKeyPressed += ToggleLine;
        _host.Tray.SessionEnding += OnSessionEnding;
        _host.Rope.Mouse += OnRopeMouse;

        StartFolderWatchers();
        _host.Tray.SetTooltip("ClipLine — 截个图，它就会挂在这里");
        Diag($"attach: clipboard={_clipboardAttached} hotkey={_hotKeyRegistered} " +
             $"syncContext={_context?.GetType().Name ?? "null"} watchers={_watchers.Count}");
    }

    // ------------------------------------------------------------------
    // 目录监听：系统默认截图目录 + ClipLine 自有目录
    // ------------------------------------------------------------------

    private void StartFolderWatchers()
    {
        foreach (string directory in new[]
                 {
                     _host.Environment.ScreenshotsFolder,
                     _host.Environment.OwnDirectory,
                 }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(directory);
                var watcher = new FolderWatcher();
                watcher.Changed += (path, change, now) =>
                {
                    Diag($"fs event: {change} {path}");
                    Post(() => OnFolderChanged(path, change, directory, now));
                };
                watcher.Start(directory);
                _watchers.Add(watcher);
                Diag("watch started: " + directory);
            }
            catch (Exception ex)
            {
                Diag($"watch FAILED: {directory} {ex.GetType().Name}: {ex.Message}");
                // 目录不可读不影响托盘与主链路；真实错误在诊断日志里单列。
            }
        }
    }

    private void OnFolderChanged(string path, FolderChange change, string directory, long nowMs)
    {
        Diag($"folder handle: {change} {path}");
        if (change is not (FolderChange.Added or FolderChange.RenamedNewName or FolderChange.Modified)) return;
        if (!File.Exists(path) || !CaptureIntegrity.IsSupported(path)) return;
        bool isSystemFolder = string.Equals(directory, _host.Environment.ScreenshotsFolder,
            StringComparison.OrdinalIgnoreCase);
        if (isSystemFolder && !_host.Facade.Current.InboxEnabled) return;

        // 0.2 秒防抖：截图工具常先建空文件再写入。
        if (_seen.TryGetValue(path, out long last) && nowMs - last < FolderWatcher.DebounceMs) return;
        _seen[path] = nowMs;

        try
        {
            long size = new FileInfo(path).Length;
            if (size <= 0) return;
            Diag("folder -> NewItem: " + path);
            _host.Facade.Handle(new NewItem(path, ItemOrigin.Screenshot,
                IsRecording: CaptureIntegrity.IsVideo(path), InOwnDirectory: false));
            _host.Facade.AdvanceTo(Environment.TickCount64);
            _host.EnsurePresented();
            _host.RenderFrame();
        }
        catch (Exception)
        {
            // 单个坏文件不能拖垮常驻进程。
        }
    }

    // ------------------------------------------------------------------
    // 剪贴板：CF_BITMAP 优先，缺位时走 CF_DIB（Win+Shift+S 常走这条）
    // ------------------------------------------------------------------

    private void OnClipboardUpdated()
    {
        if (!_host.Facade.Current.ClipboardEnabled) return;
        if (!ShellNative.OpenClipboard(IntPtr.Zero)) return;

        IntPtr lockedDib = IntPtr.Zero;
        IntPtr hDib = IntPtr.Zero;
        try
        {
            IntPtr hBitmap = ShellNative.GetClipboardData(ShellNative.CF_BITMAP);
            if (hBitmap == IntPtr.Zero)
            {
                hDib = ShellNative.GetClipboardData(ShellNative.CF_DIB);
                if (hDib == IntPtr.Zero) return;
                lockedDib = ShellNative.GlobalLock(hDib);
                if (lockedDib == IntPtr.Zero) return;
            }

            string path = Path.Combine(_host.Environment.OwnDirectory,
                $"clip-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            bool saved = hBitmap != IntPtr.Zero
                ? GdiPlus.SaveBitmapAsPng(hBitmap, path)
                : GdiPlus.SaveDibAsPng(lockedDib, path);
            if (!saved)
            {
                TryDelete(path);
                return;
            }

            byte[] bytes = File.ReadAllBytes(path);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (hash == _lastClipboardHash)
            {
                TryDelete(path);
                return;
            }
            _lastClipboardHash = hash;

            Diag("clipboard -> NewItem: " + path);
            _host.Facade.Handle(new NewItem(path, ItemOrigin.Clipboard, InOwnDirectory: true));
            _host.Facade.AdvanceTo(Environment.TickCount64);
            _host.EnsurePresented();
            _host.RenderFrame();
            StateSnapshot s = _host.Facade.Current;
            Diag($"window: present={s.IsPresent} revealed={s.IsRevealed} " +
                  $"exists={_host.Rope.Exists} visible={_host.Rope.IsVisible} " +
                  $"hwnd=0x{_host.Rope.Handle.ToInt64():X} cards={s.Items.Count}");
        }
        catch (Exception)
        {
            // 剪贴板被别的进程独占是正常情况，等下一次 WM_CLIPBOARDUPDATE。
        }
        finally
        {
            if (lockedDib != IntPtr.Zero && hDib != IntPtr.Zero) ShellNative.GlobalUnlock(hDib);
            ShellNative.CloseClipboard();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    // ------------------------------------------------------------------
    // 托盘 16 项菜单：每次打开重建（T-05），中文本地化，子菜单递归
    // ------------------------------------------------------------------

    private void ShowTrayMenu()
    {
        StateSnapshot state = _host.Facade.Current;
        // T-05：让门面记录一次「菜单打开」，同时保持线展开。
        _host.Facade.Handle(new TrayMenuOpening());
        _host.Facade.Handle(new MenuToggled(true));

        var context = new TrayMenuContext(
            state.LiveCount,
            state.UndoCount > 0,
            state.Items.Any(i => i.Held),
            state.InboxEnabled,
            state.ClipboardEnabled,
            state.RevealOnPointer,
            state.PeekOnNewContent,
            state.ShowOverFullScreen,
            state.KeepOnLine,
            state.Size,
            state.SoundOn,
            state.LaunchAtLogin,
            state.ExtraFolders,
            state.Shortcut,
            state.IsRevealed);

        _menuEntries.Clear();
        int nextId = 1000;
        IntPtr menu = BuildMenu(TrayMenuModel.Build(context), context.Shortcut, _menuEntries, ref nextId);
        if (menu == IntPtr.Zero) return;

        ShellNative.GetCursorPos(out ShellNative.POINT cursor);
        ShellNative.SetForegroundWindow(_host.Tray.HostHandle);
        try
        {
            int selected = ShellNative.TrackPopupMenuEx(menu,
                ShellNative.TPM_RETURNCMD | ShellNative.TPM_RIGHTBUTTON | ShellNative.TPM_NONOTIFY,
                cursor.X, cursor.Y, _host.Tray.HostHandle, IntPtr.Zero);
            if (selected > 0)
            {
                MenuEntry? entry = _menuEntries.Find(e => e.Id == selected);
                if (entry is not null) InvokeTrayAction(entry);
            }
        }
        finally
        {
            ShellNative.DestroyMenu(menu);
            _host.Facade.Handle(new MenuToggled(false));
        }
    }

    /// <summary>
    /// 16 项托盘菜单的 Win32 结构自检：不弹窗、不需要交互，验证同一套
    /// CreatePopupMenu / AppendMenuW / DestroyMenu API 路径与中文本地化。
    /// 由 --smoke 调用，覆盖「真实输入桥」这条不进 128 项纯逻辑测试的链路。
    /// </summary>
    internal static (bool Pass, string Detail) SelfTest()
    {
        var context = new TrayMenuContext(3, true, false, true, true, true, false, false,
            0, CardSize.Medium, true, false, Array.Empty<string>(), "Ctrl+Alt+T", false);
        IReadOnlyList<TrayMenuSpec> specs = TrayMenuModel.Build(context);
        var entries = new List<MenuEntry>();
        int nextId = 1000;
        IntPtr menu = BuildMenu(specs, context.Shortcut, entries, ref nextId);
        if (menu == IntPtr.Zero) return (false, "CreatePopupMenu 返回空句柄");
        try
        {
            int count = ShellNative.GetMenuItemCount(menu);
            bool localized = Label("Show Line") == "显示晾衣绳";
            // Sound 与 Quit 两项带 SeparatorBefore，分隔符也各占一个菜单项。
            int expected = specs.Count + specs.Count(s => s.SeparatorBefore);
            bool pass = count == expected
                        && entries.Count > TrayMenuModel.MainItemCount
                        && localized;
            return (pass, $"主菜单项={count}/{expected}(含分隔符), 可选命令={entries.Count}, 中文标签={localized}");
        }
        finally { ShellNative.DestroyMenu(menu); }
    }

    private static IntPtr BuildMenu(IEnumerable<TrayMenuSpec> specs, string shortcut,
                                    List<MenuEntry> entries, ref int nextId)
    {
        IntPtr menu = ShellNative.CreatePopupMenu();
        if (menu == IntPtr.Zero) return IntPtr.Zero;
        foreach (TrayMenuSpec spec in specs)
        {
            if (spec.SeparatorBefore)
                ShellNative.AppendMenuW(menu, ShellNative.MF_SEPARATOR, IntPtr.Zero, null);

            string label = Label(spec.Key);
            if (spec.Action == TrayMenuAction.ToggleLine && !string.IsNullOrEmpty(shortcut))
                label += "\t" + shortcut;

            if (spec.Children is { Count: > 0 })
            {
                IntPtr submenu = BuildMenu(spec.Children, shortcut, entries, ref nextId);
                if (submenu != IntPtr.Zero)
                    ShellNative.AppendMenuW(menu, ShellNative.MF_POPUP, submenu, label);
                continue;
            }

            uint flags = ShellNative.MF_STRING;
            if (!spec.Enabled) flags |= ShellNative.MF_GRAYED | ShellNative.MF_DISABLED;
            if (spec.Checked) flags |= ShellNative.MF_CHECKED;
            int id = nextId++;
            string? argument = spec.Action switch
            {
                TrayMenuAction.WatchFolder or TrayMenuAction.StopWatching => spec.Key,
                _ => null,
            };
            entries.Add(new MenuEntry(id, spec.Action, argument));
            ShellNative.AppendMenuW(menu, flags, new IntPtr(id), label);
        }
        return menu;
    }

    private void InvokeTrayAction(MenuEntry entry)
    {
        _host.Facade.Handle(new TrayMenuItemInvoked(entry.Action, entry.Argument));
        _host.Facade.AdvanceTo(Environment.TickCount64);
        _host.RenderFrame();
        if (entry.Action == TrayMenuAction.Quit) _host.RequestQuit();
    }

    private static readonly Dictionary<string, string> ExtraChinese = new(StringComparer.Ordinal)
    {
        ["Pointer at Menu Bar"] = "指针靠近菜单栏时",
        ["New Content"] = "有新内容时",
        ["Over Full Screen Apps"] = "在全屏应用之上",
        ["As Many As Fit"] = "放得下的数量",
        ["Small"] = "小",
        ["Medium"] = "中",
        ["Large"] = "大",
        ["Add Folder…"] = "添加文件夹…",
        ["Stop Watching"] = "停止监视",
        ["Change Shortcut…"] = "更改快捷键…",
        ["Watch Folders"] = "同时监视文件夹",
    };

    /// <summary>菜单标签统一走 P-03 本地化表；未收录的补充词在 ExtraChinese 里。</summary>
    private static string Label(string key)
    {
        string bare = key.Replace(" ▸", string.Empty, StringComparison.Ordinal);
        string localized = Localization.Get(bare, "zh-Hans");
        if (localized != bare) return localized;
        return ExtraChinese.TryGetValue(bare, out string? extra) ? extra : bare;
    }

    // ------------------------------------------------------------------
    // 卡片真实鼠标交互
    // ------------------------------------------------------------------

    private void OnRopeMouse(RopeMouseEvent e)
    {
        if (e.CardIndex < 0 || e.ItemId == Guid.Empty) return;
        switch (e.Kind)
        {
            case RopeMouseKind.Down:
                _host.Facade.Handle(new CardPressedChanged(true));
                _host.Facade.Handle(new CardPointerDown(e.ItemId, e.X, e.Y));
                break;
            case RopeMouseKind.Up:
                _host.Facade.Handle(new CardPointerUp(e.ItemId, e.X, e.Y));
                _host.Facade.Handle(new CardPressedChanged(false));
                break;
            case RopeMouseKind.DoubleClick:
                _host.Facade.Handle(new ForceClickEquivalent(e.ItemId));
                break;
            case RopeMouseKind.RightClick:
                _contextItemId = e.ItemId;
                _host.Facade.Handle(new ContextMenuRequested(e.ItemId));
                break;
            case RopeMouseKind.Wheel:
                _host.Facade.Handle(new Scroll(e.WheelDelta / 120.0, false));
                break;
        }
        _host.Facade.AdvanceTo(Environment.TickCount64);
        _host.RenderFrame();
    }

    /// <summary>门面发出的交互命令 → Win32 菜单 / 系统默认应用 / 剪贴板 / 气泡。</summary>
    internal void HandleCommand(AppCommand command)
    {
        string argument = command.Argument ?? string.Empty;
        switch (command.Kind)
        {
            case CommandKind.ShowWelcome:
                _host.Tray.ShowBalloon("欢迎使用 ClipLine", "截个图，它就会挂在这里。右键托盘图标可打开设置菜单。");
                break;

            case CommandKind.ShowInboxOffer:
                // C-01 / F-04：两个捕获源按规格是 opt-in，首次运行明确告知开启入口，
                // 不擅自替用户改写设置。
                _host.Tray.ShowBalloon("开启截图挂绳",
                    "右键托盘图标 → 勾选「挂上复制的图片」与「接管截图」，之后截图和复制的图片就会自动挂上。");
                break;

            case CommandKind.ShowQuickLook:
            case CommandKind.ShowMarkupEditor:
            case CommandKind.ShowTrimEditor:
            case CommandKind.ShowHeldWindow:
                OpenDefaultApp(ResolvePath(argument));
                break;

            case CommandKind.ShowContextMenu:
                ShowCardContextMenu();
                break;

            case CommandKind.WriteClipboardImage:
            case CommandKind.WriteClipboardFile:
                string? copyPath = ResolvePath(argument);
                if (!string.IsNullOrEmpty(copyPath)) GdiPlus.CopyFileToClipboard(copyPath);
                break;

            case CommandKind.PlaySound:
            case CommandKind.Beep:
                ShellNative.MessageBeep(ShellNative.MbOk);
                break;

            case CommandKind.ShowFolderPicker:
                string? folder = PickFolder();
                if (!string.IsNullOrEmpty(folder))
                {
                    _host.Facade.Handle(new TrayMenuItemInvoked(TrayMenuAction.AddFolder, folder));
                    _host.Facade.AdvanceTo(Environment.TickCount64);
                }
                break;

            case CommandKind.ShowShortcutRecorder:
                _host.Tray.ShowBalloon("ClipLine 快捷键", "v1 固定为 Ctrl+Alt+T；自定义录制器将在后续版本提供。");
                break;

            default:
                break; // 动画 / 设置 / 主题类命令由平台层或后续渲染迭代处理
        }
    }

    private string? ResolvePath(string argument)
    {
        if (!string.IsNullOrEmpty(argument) && File.Exists(argument)) return argument;
        foreach (CardVisual card in _host.CurrentView().Cards)
            if (card.Id == _contextItemId) return card.Path;
        return null;
    }

    private static void OpenDefaultApp(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        ShellNative.ShellExecuteW(IntPtr.Zero, "open", path, null, null, ShellNative.SwShowNormal);
    }

    private static readonly (ContextAction Action, string Label)[] CardMenuItems =
    {
        (ContextAction.Copy, "复制"),
        (ContextAction.Open, "打开"),
        (ContextAction.QuickLook, "快速查看"),
        (ContextAction.Hold, "拿在手上"),
        (ContextAction.Markup, "标记"),
        (ContextAction.RevealInExplorer, "在文件资源管理器中显示"),
        (ContextAction.Pin, "固定"),
        (ContextAction.Unpin, "取消固定"),
        (ContextAction.StoreToDesktop, "存储到桌面"),
        (ContextAction.TakeDown, "取下"),
        (ContextAction.TakeDownLeft, "取下左侧全部"),
        (ContextAction.TakeDownRight, "取下右侧全部"),
        (ContextAction.Discard, "丢弃"),
        (ContextAction.MoveToRecycleBin, "移到回收站"),
    };

    private void ShowCardContextMenu()
    {
        if (_contextItemId == Guid.Empty) return;
        IntPtr menu = ShellNative.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        var actions = new Dictionary<int, ContextAction>();
        int id = 2000;
        bool kept = _host.CurrentView().Cards.Any(c => c.Id == _contextItemId && c.Kept);
        foreach ((ContextAction action, string label) in CardMenuItems)
        {
            if (action == ContextAction.Pin && kept) continue;
            if (action == ContextAction.Unpin && !kept) continue;
            if (action == ContextAction.TakeDownRight)
                ShellNative.AppendMenuW(menu, ShellNative.MF_SEPARATOR, IntPtr.Zero, null);
            actions[id] = action;
            ShellNative.AppendMenuW(menu, ShellNative.MF_STRING, new IntPtr(id), label);
            id++;
        }

        ShellNative.GetCursorPos(out ShellNative.POINT cursor);
        ShellNative.SetForegroundWindow(_host.Tray.HostHandle);
        try
        {
            int selected = ShellNative.TrackPopupMenuEx(menu,
                ShellNative.TPM_RETURNCMD | ShellNative.TPM_RIGHTBUTTON | ShellNative.TPM_NONOTIFY,
                cursor.X, cursor.Y, _host.Tray.HostHandle, IntPtr.Zero);
            if (selected > 0 && actions.TryGetValue(selected, out ContextAction action))
            {
                _host.Facade.Handle(new ContextMenuItem(_contextItemId, action));
                _host.Facade.AdvanceTo(Environment.TickCount64);
                _host.RenderFrame();
            }
        }
        finally
        {
            ShellNative.DestroyMenu(menu);
            _host.Facade.Handle(new MenuToggled(false));
        }
    }

    // ------------------------------------------------------------------
    // 杂项
    // ------------------------------------------------------------------

    private void ToggleLine()
    {
        _host.Facade.Handle(new ToggleLine());
        _host.Facade.AdvanceTo(Environment.TickCount64);
        _host.EnsurePresented();
        _host.RenderFrame();
    }

    private void OnSessionEnding()
    {
        _host.Facade.Handle(new ApplicationExiting());
        _host.RequestQuit();
    }

    private void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(static state => ((Action)state!)(), action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IntPtr hwnd = _host.Tray.HostHandle;
        if (_clipboardAttached && hwnd != IntPtr.Zero) ShellNative.RemoveClipboardFormatListener(hwnd);
        if (_hotKeyRegistered && hwnd != IntPtr.Zero) ShellNative.UnregisterHotKey(hwnd, 1);

        _host.Tray.MenuRequested -= ShowTrayMenu;
        _host.Tray.LeftClicked -= ToggleLine;
        _host.Tray.ClipboardUpdated -= OnClipboardUpdated;
        _host.Tray.HotKeyPressed -= ToggleLine;
        _host.Tray.SessionEnding -= OnSessionEnding;
        _host.Rope.Mouse -= OnRopeMouse;
        foreach (FolderWatcher watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }

    // ---- 文件夹选择：SHBrowseForFolderW，零 NuGet、零 COM 注册 ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFOW
    {
        public IntPtr HwndOwner;
        public IntPtr PidlRoot;
        public IntPtr PszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszTitle;
        public uint UlFlags;
        public IntPtr Lpfn;
        public IntPtr LParam;
        public int Image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    private static string? PickFolder()
    {
        IntPtr display = Marshal.AllocHGlobal(260 * 2);
        try
        {
            var info = new BROWSEINFOW
            {
                HwndOwner = IntPtr.Zero,
                PidlRoot = IntPtr.Zero,
                PszDisplayName = display,
                LpszTitle = "选择要同时监视的文件夹",
                UlFlags = 0x0001, // BIF_RETURNONLYFSDIRS
            };
            IntPtr pidl = SHBrowseForFolderW(ref info);
            if (pidl == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(260);
                return SHGetPathFromIDListW(pidl, buffer) ? buffer.ToString() : null;
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        catch (Exception) { return null; }
        finally { Marshal.FreeHGlobal(display); }
    }
}
