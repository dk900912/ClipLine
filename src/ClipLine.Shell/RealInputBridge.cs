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
    private readonly Dictionary<string, FolderWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MenuEntry> _menuEntries = new();
    private string? _lastClipboardHash;
    // 自己写剪贴板（单击复制 / 右键复制）会触发 WM_CLIPBOARDUPDATE。
    // 吞掉紧随其后的 1–2 条通知，否则每复制一次就自动再挂一张自己的副本。
    private int _selfClipboardWrites;
    private long _selfClipboardUntilMs;
    private Guid _contextItemId;
    /// <summary>键盘唤出菜单时的锚点（客户区 = 屏幕坐标，绳窗口铺满整屏）；鼠标唤出为 null。</summary>
    private ShellNative.POINT? _menuAnchor;
    private bool _clipboardAttached;
    private bool _hotKeyRegistered;
    private bool _disposed;
    private string _shortcut = HotKeyModel.DefaultShortcut;
    private ShortcutWindow? _shortcutWindow;

    /// <summary>全局热键 id：1=晾衣绳开关，2=退出（菜单里承诺的 Ctrl+Q）。</summary>
    private const int ToggleHotKeyId = 1;
    private const int QuitHotKeyId = 2;

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
        // 快捷键的注册/注销只有这一套（唯一所有者）：门面发 RegisterHotKey 时
        // 也走本类，否则平台层会在自己的隐藏窗口上再注册一次并静默失败。
        _shortcut = _host.Facade.Current.Shortcut;
        _hotKeyRegistered = RegisterToggleHotKey(_shortcut);
        // P1-4：菜单里写着 Ctrl+Q，就必须真的能按。
        ShellNative.RegisterHotKey(hwnd, QuitHotKeyId, ShellNative.MOD_CONTROL, ShellNative.VkQ);

        _host.Tray.MenuRequested += ShowTrayMenu;
        _host.Tray.LeftClicked += ToggleLine;
        _host.Tray.ClipboardUpdated += OnClipboardUpdated;
        _host.Tray.HotKeyPressed += OnGlobalHotKey;
        _host.Tray.SessionEnding += OnSessionEnding;
        _host.Tray.ThemeChanged += OnSystemThemeChanged;
        _host.Rope.Mouse += OnRopeMouse;
        _host.Rope.Key += OnRopeKey;
        _host.Rope.Drag += OnRopeDrag;
        RopeWindow.Trace = Diag;
        ToolWindow.Trace = Diag;

        SyncWatchers();
        _host.Tray.SetTooltip("ClipLine — 截个图，它就会挂在这里");
        Diag($"attach: clipboard={_clipboardAttached} hotkey={_hotKeyRegistered} " +
             $"shortcut=\"{_shortcut}\" " +
             $"syncContext={_context?.GetType().Name ?? "null"} watchers={_watchers.Count}");
        if (!_hotKeyRegistered)
        {
            // P1-6：注册失败必须让用户看见，而不是只写一行诊断日志。
            _host.Tray.ShowBalloon("快捷键被占用",
                $"{_shortcut} 已被其他应用占用。请在托盘菜单「快捷键」中更换。");
        }
    }

    /// <summary>系统深浅色 / 高对比度切换：重刷色板并让所有自绘窗口立即换色。</summary>
    private void OnSystemThemeChanged() => _host.ApplyTheme();

    /// <summary>注册晾衣绳开关热键；失败返回 false，由调用方决定如何告知用户。</summary>
    private bool RegisterToggleHotKey(string shortcut)
    {
        IntPtr hwnd = _host.Tray.HostHandle;
        if (hwnd == IntPtr.Zero) return false;
        ShellNative.UnregisterHotKey(hwnd, ToggleHotKeyId);
        _hotKeyRegistered = false;
        if (string.IsNullOrWhiteSpace(shortcut) ||
            shortcut.Equals(HotKeyModel.NoneShortcut, StringComparison.Ordinal)) return false;
        string[] parts = shortcut.Split('+', StringSplitOptions.TrimEntries);
        string key = parts[^1];
        if (key.Length != 1) return false;
        uint modifiers = HotKey.ParseModifiers(shortcut);
        uint vk = char.ToUpperInvariant(key[0]);
        // 少数输入法 / 远程桌面会拒绝 NOREPEAT，退回普通注册再判一次。
        _hotKeyRegistered =
            ShellNative.RegisterHotKey(hwnd, ToggleHotKeyId, modifiers | ShellNative.MOD_NOREPEAT, vk)
            || ShellNative.RegisterHotKey(hwnd, ToggleHotKeyId, modifiers, vk);
        Diag($"hotkey: register \"{shortcut}\" -> {_hotKeyRegistered}");
        return _hotKeyRegistered;
    }

    /// <summary>WM_HOTKEY 按 id 分派：Ctrl+Alt+T 开关绳，Ctrl+Q 退出。</summary>
    private void OnGlobalHotKey(int id)
    {
        if (id == QuitHotKeyId) { _host.RequestQuit(); return; }
        ToggleLine();
    }

    // ------------------------------------------------------------------
    // 目录监听：系统默认截图目录 + ClipLine 自有目录
    // ------------------------------------------------------------------

    /// <summary>
    /// 按门面当前设置同步唯一一套目录监听器：系统截图目录 + 自有目录 + ExtraFolders。
    /// 托盘菜单「添加文件夹 / 停止监视」必须走这里，否则会出现平台层另建一套、
    /// 菜单改了设置而真实监听纹丝不动的情况。
    /// </summary>
    private void SyncWatchers()
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[]
                 {
                     _host.Environment.ScreenshotsFolder,
                     _host.Environment.OwnDirectory,
                 }.Concat(_host.Facade.Current.ExtraFolders))
        {
            if (!string.IsNullOrWhiteSpace(directory)) desired.Add(directory);
        }

        foreach ((string directory, FolderWatcher watcher) in
                 _watchers.Where(pair => !desired.Contains(pair.Key)).ToArray())
        {
            watcher.Dispose();
            _watchers.Remove(directory);
            Diag("watch stopped: " + directory);
        }

        foreach (string directory in desired.Where(d => !_watchers.ContainsKey(d)))
        {
            try
            {
                Directory.CreateDirectory(directory);
                var watcher = new FolderWatcher();
                string captured = directory;
                watcher.Changed += (path, change, now) =>
                {
                    Diag($"fs event: {change} {path}");
                    Post(() => OnFolderChanged(path, change, captured, now));
                };
                watcher.Start(directory);
                _watchers[directory] = watcher;
                Diag("watch started: " + directory);
            }
            catch (Exception ex)
            {
                Diag($"watch FAILED: {directory} {ex.GetType().Name}: {ex.Message}");
                // 目录不可读不影响托盘与主链路；真实错误在诊断日志里单列。
            }
        }
    }

    /// <summary>「挂上复制的图片」开关：真正挂上/摘掉 WM_CLIPBOARDUPDATE 监听。</summary>
    private void SyncClipboardListener()
    {
        IntPtr hwnd = _host.Tray.HostHandle;
        if (hwnd == IntPtr.Zero) return;
        bool want = _host.Facade.Current.ClipboardEnabled;
        if (want && !_clipboardAttached)
            _clipboardAttached = ShellNative.AddClipboardFormatListener(hwnd);
        else if (!want && _clipboardAttached && ShellNative.RemoveClipboardFormatListener(hwnd))
            _clipboardAttached = false;
        Diag($"clipboard listener: attached={_clipboardAttached} enabled={want}");
    }

    private void OnFolderChanged(string path, FolderChange change, string directory, long nowMs)
    {
        Diag($"folder handle: {change} {path}");

        if (FolderWatcher.IsRemoval(change))
        {
            // 目录里的图片被删掉 / 改名，绳上不能留幽灵卡片。
            // 只对确实挂在绳上的路径发事件，避免无关文件删除制造状态与落盘噪音。
            bool onLine = _host.Facade.Current.Items.Any(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (!onLine) return;
            _seen.Remove(path); // 同名文件再次创建时可以被立刻挂上
            Diag("folder -> FileDisappeared: " + path);
            _host.Facade.Handle(new FileDisappeared(path));
            _host.AdvanceFrame();
            _host.RenderFrame();
            return;
        }

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
            _host.AdvanceFrame();
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
        if (_selfClipboardWrites > 0)
        {
            if (Environment.TickCount64 <= _selfClipboardUntilMs)
            {
                _selfClipboardWrites--;
                Diag("clipboard: ignored self-written update");
                return;
            }
            _selfClipboardWrites = 0; // 超时作废，绝不吞掉用户之后的外部复制
        }
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
            _host.AdvanceFrame();
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
                ShellNative.TPM_RETURNCMD | ShellNative.TPM_RIGHTBUTTON | ShellNative.TPM_NONOTIFY
                | ShellNative.TPM_WORKAREA,
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
            // P0-6：按 Alt 后能用字母直接跳到菜单项 —— 统计带「(&X)」的项。
            int withAccessKey = 0;
            for (uint i = 0; i < count; i++)
            {
                var text = new StringBuilder(256);
                if (ShellNative.GetMenuStringW(menu, i, text, text.Capacity, 0x0400) > 0
                    && text.ToString().Contains("(&", StringComparison.Ordinal))
                    withAccessKey++;
            }
            bool accessKeys = withAccessKey >= 12;
            bool pass = count == expected
                        && entries.Count > TrayMenuModel.MainItemCount
                        && localized && accessKeys;
            return (pass, $"主菜单项={count}/{expected}(含分隔符), 可选命令={entries.Count}, " +
                          $"中文标签={localized}, 访问键={withAccessKey}");
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
            // 动态文件夹列表不占访问键；其余按稳定 order 分配，同层不重复。
            label = LabelWithAccessKey(label, TrayAccessKey(spec.Order));
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
        _host.AdvanceFrame();
        _host.RenderFrame();
        if (entry.Action == TrayMenuAction.Quit) { _host.RequestQuit(); return; }
        // 设置类菜单（接管截图 / 挂上复制的图片 / 监视文件夹）改完后，
        // 立刻让真实监听器与门面状态一致，而不是只写进 state.json。
        SyncWatchers();
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

    /// <summary>
    /// P0-6 / P1-8：菜单项补键盘访问键。中文标签后追加「(&amp;X)」，
    /// 按 Alt 后按字母即可跳到该项，不必抓着鼠标。同一级菜单内保证不重复。
    /// </summary>
    private static string LabelWithAccessKey(string label, char? key) =>
        key is { } ch ? $"{label}(&{ch})" : label;

    /// <summary>托盘菜单的访问键按稳定 order 分配（动作枚举有复用，不能按动作取）。</summary>
    private static char? TrayAccessKey(int order) => order switch
    {
        1 => 'S',   // Show / Hide Line
        2 => 'T',   // Take Down All
        3 => 'P',   // Put Back
        4 => 'H',   // Hang Everything Back
        5 => 'K',   // Take Over Screenshots
        6 => 'C',   // Hang Copied Images
        7 => 'L',   // Lower the Line
        8 => 'F',   // Watch Folders
        9 => 'O',   // Keep on Line
        10 => 'Z',  // Size
        11 => 'U',  // Shortcut
        12 => 'D',  // Open Screenshots Folder
        13 => 'N',  // Sound
        14 => 'A',  // Launch at Login
        15 => 'V',  // Check for Updates
        16 => 'Q',  // Quit
        71 => 'P', 72 => 'N', 73 => 'F',
        89 => 'A', 90 => 'S',
        91 => 'A', 92 => '2', 93 => '5', 94 => '1',
        101 => 'S', 102 => 'M', 103 => 'L',
        _ => null, // 动态的已监视文件夹列表不占访问键
    };

    private static char? CardAccessKey(ContextAction action) => action switch
    {
        ContextAction.Copy => 'C',
        ContextAction.Open => 'O',
        ContextAction.QuickLook => 'Q',
        ContextAction.Hold => 'H',
        ContextAction.Markup => 'M',
        ContextAction.RevealInExplorer => 'E',
        ContextAction.Pin => 'P',
        ContextAction.Unpin => 'U',
        ContextAction.StoreToDesktop => 'D',
        ContextAction.TakeDown => 'T',
        ContextAction.TakeDownLeft => 'L',
        ContextAction.TakeDownRight => 'R',
        ContextAction.Discard => 'S',
        ContextAction.MoveToRecycleBin => 'B',
        _ => null,
    };

    // ------------------------------------------------------------------
    // 卡片真实鼠标交互
    // ------------------------------------------------------------------

    /// <summary>internal 供冒烟直接派发 WM 转换后的事件，覆盖「消息 → 门面」映射层。</summary>
    internal void OnRopeMouse(RopeMouseEvent e)
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
        _host.AdvanceFrame();
        _host.RenderFrame();
    }

    /// <summary>
    /// 卡片被拖出后的落点处理（P0-5）。文件还在（复制 / 取消）就什么都不做，
    /// 文件已经离开原路径就交给门面的 FileDisappeared —— 那条路径本来就在监视
    /// 「文件被外部删除」，语义完全一致：立即下线 + 播放掉落动画。
    /// </summary>
    internal void OnRopeDrag(RopeDragEvent e)
    {
        Diag($"drag: \"{e.Path}\" -> {e.Outcome}");
        if (e.Outcome == DragOutcome.Moved && !File.Exists(e.Path))
            _host.Facade.Handle(new FileDisappeared(e.Path));
        _host.AdvanceFrame();
        _host.RenderFrame();
    }

    /// <summary>
    /// 键盘操作（P0-6）：Tab 与方向键已在窗口内部换成焦点移动，这里只处理动作键。
    /// 全部走既有输入（KeyBorrow / TrayMenuItemInvoked / ContextMenuRequested），
    /// 不新增门面输入，唯一测试接缝保持不变。
    /// </summary>
    private void OnRopeKey(RopeKeyEvent e)
    {
        bool hasCard = e.ItemId != Guid.Empty;
        switch (e.VirtualKey)
        {
            case ShellNative.VkEscape:
                _host.Facade.Handle(new ToggleLine()); // 收起晾衣绳，交还焦点
                break;
            case ShellNative.VkEnter when hasCard:
            case ShellNative.VkC when e.Ctrl && hasCard:
                _host.Facade.Handle(new KeyBorrow(CardKey.Copy, e.ItemId));
                break;
            case ShellNative.VkSpace when hasCard:
                _host.Facade.Handle(new KeyBorrow(CardKey.Space, e.ItemId));
                break;
            case ShellNative.VkDelete when hasCard:
                _host.Facade.Handle(new KeyBorrow(CardKey.Delete, e.ItemId));
                break;
            case ShellNative.VkZ when e.Ctrl:
                // 菜单里写的 Ctrl+Z 现在真的可用（绳子展开期间）。
                _host.Facade.Handle(new TrayMenuItemInvoked(TrayMenuAction.PutBack, null));
                break;
            case ShellNative.VkF10 when e.Shift && hasCard:
            case ShellNative.VkApps when hasCard:
                _contextItemId = e.ItemId;
                // 键盘唤出的菜单要贴着焦点卡片弹，而不是跟着鼠标跑。
                var rects = _host.Rope.CardRects;
                if (e.CardIndex >= 0 && e.CardIndex < rects.Count)
                {
                    var card = rects[e.CardIndex];
                    _menuAnchor = new ShellNative.POINT
                    {
                        X = card.X + card.W / 2,
                        Y = card.Y + card.H - 8,
                    };
                }
                _host.Facade.Handle(new ContextMenuRequested(e.ItemId));
                break;
            case ShellNative.VkQ when e.Ctrl:
                _host.RequestQuit();
                return;
        }
        _host.AdvanceFrame();
        _host.RenderFrame();
    }

    /// <summary>系统主题变化：录制器也要跟着换色（它不由 ShellAppHost 直接持有）。</summary>
    internal void ApplyTheme() => _shortcutWindow?.ApplyTheme();

    /// <summary>门面发出的交互命令 → Win32 菜单 / 系统默认应用 / 剪贴板 / 气泡。</summary>
    internal void HandleCommand(AppCommand command)
    {
        string argument = command.Argument ?? string.Empty;
        switch (command.Kind)
        {
            case CommandKind.RegisterHotKey:
                // 门面保存的是「用户想要的值」，能不能真注册上只有这一层知道；
                // 失败必须回灌 HotKeyConflict，让设置回滚并给出可见提示（P1-6）。
                if (string.IsNullOrEmpty(argument)) break;
                if (RegisterToggleHotKey(argument))
                {
                    _shortcut = argument;
                    break;
                }
                _host.Tray.ShowBalloon("快捷键被占用",
                    $"{argument} 已被其他应用占用，已恢复上一个快捷键。");
                _host.Facade.Handle(new HotKeyConflict(argument));
                _host.AdvanceFrame();
                _host.RenderFrame();
                break;
            case CommandKind.UnregisterHotKey:
                ShellNative.UnregisterHotKey(_host.Tray.HostHandle, ToggleHotKeyId);
                _hotKeyRegistered = false;
                break;
            case CommandKind.RollbackShortcut:
                if (!string.IsNullOrEmpty(argument) && RegisterToggleHotKey(argument))
                    _shortcut = argument;
                break;

            case CommandKind.ShowShortcutRecorder:
                _shortcutWindow ??= new ShortcutWindow(_host.Scale, _host.Facade);
                _shortcutWindow.Show();
                Diag("shortcut recorder: opened");
                break;
            // 方案 D：更新是同一条「门面发命令 → 输入桥落到真实副作用」链路。
            case CommandKind.ShowUpdateNotice:
                _host.Tray.ShowBalloon("ClipLine 更新", argument);
                break;
            case CommandKind.RunUpdateCheck:
                _host.Updates.RunCheck(argument);
                break;
            case CommandKind.DownloadUpdate:
                _host.Updates.Download(argument);
                break;
            case CommandKind.InstallUpdate:
                _host.Updates.Install(argument);
                break;
            case CommandKind.CancelUpdateDownload:
                _host.Updates.CancelDownload();
                break;
            case CommandKind.HideUpdatePanel:
                _host.UpdatePanel?.Hide();
                break;

            case CommandKind.ShowWelcome:
                _host.Tray.ShowBalloon("欢迎使用 ClipLine", "截个图，它就会挂在这里。右键托盘图标可打开设置菜单。");
                break;

            case CommandKind.ShowInboxOffer:
                // C-01 / F-04：两个捕获源按规格是 opt-in，首次运行明确告知开启入口，
                // 不擅自替用户改写设置。
                _host.Tray.ShowBalloon("开启截图挂绳",
                    "在托盘菜单勾选两项捕获开关，即可自动挂上截图与复制的图片。");
                break;

            case CommandKind.ShowQuickLook:
            case CommandKind.ShowMarkupEditor:
            case CommandKind.ShowTrimEditor:
            case CommandKind.ShowHeldWindow:
            {
                // v1.4：四个菜单项各自打开自己的自绘窗口，不再统一降级到
                // ShellExecuteW("open")（那正是「点了效果都一样」的根因）。
                ToolMode mode = command.Kind switch
                {
                    CommandKind.ShowQuickLook => ToolMode.QuickLook,
                    CommandKind.ShowMarkupEditor => ToolMode.Markup,
                    CommandKind.ShowTrimEditor => ToolMode.Trim,
                    _ => ToolMode.Held,
                };
                (string? path, Guid id, int index) = ResolveCard(argument);
                var paths = new List<string>();
                foreach (CardVisual card in _host.CurrentView().Cards) paths.Add(card.Path);
                _host.Tools.Show(mode, paths, index, id);
                Diag($"tool window: {mode} index={index} path={path}");
                break;
            }

            case CommandKind.CloseQuickLook:
                _host.Tools.Close(ToolMode.QuickLook);
                break;

            case CommandKind.CloseHeldWindow:
                _host.Tools.Close(ToolMode.Held);
                break;

            case CommandKind.ShowContextMenu:
                ShowCardContextMenu(argument);
                break;

            case CommandKind.WriteClipboardImage:
            case CommandKind.WriteClipboardFile:
                string? copyPath = ResolvePath(argument);
                // D-11：录屏只放文件引用；其余图片同时给位图（聊天/文档）与
                // CF_HDROP（资源管理器 Ctrl+V 粘贴文件）。
                bool withBitmap = command.Kind == CommandKind.WriteClipboardImage;
                if (!string.IsNullOrEmpty(copyPath) && GdiPlus.CopyFileToClipboard(copyPath, withBitmap))
                {
                    // 实测：EmptyClipboard + CF_BITMAP + CF_HDROP 恰好产生 3 条
                    // WM_CLIPBOARDUPDATE。计数必须精确，多留会把用户紧接着的
                    // 外部复制当成回声吞掉（e2e 曾因此偶发 CLIP_OK=False）。
                    _selfClipboardWrites = 3;
                    _selfClipboardUntilMs = Environment.TickCount64 + 2000;
                    Diag("clipboard: self copy written (bitmap+HDROP), suppress echo");
                }
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
                    _host.AdvanceFrame();
                }
                break;

            case CommandKind.ShowCopiedBadge:
                // v1.3：卡片上的「已复制」角标 + 3° 轻摆已经构成一次完整反馈，
                // 再弹通知窗是重复打扰（用户实测反馈）。角标由 ShellAppHost 按
                // 状态帧重绘，这里刻意不再转发托盘；通知窗只服务于欢迎、
                // 首次运行的捕获开关提示、快捷键说明这三类需要阅读的场景。
                break;

            case CommandKind.WatchFolder:
            case CommandKind.UnwatchFolder:
                // 真实监听器只有这一套：菜单改动后按门面 ExtraFolders 重建。
                SyncWatchers();
                break;

            case CommandKind.RegisterClipboardListener:
            case CommandKind.UnregisterClipboardListener:
                SyncClipboardListener();
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

    /// <summary>
    /// 编辑器命令的参数有两种形态：图片路径（快速查看 / 标记 / 修剪）或卡片 Id
    /// （拿在手上由门面发 <c>e.ItemId.ToString()</c>）。统一解析成「路径 + Id + 在绳上的序号」，
    /// 让窗口既能显示当前图，也能用 ← → 浏览整条绳。
    /// </summary>
    private (string? Path, Guid Id, int Index) ResolveCard(string argument)
    {
        IReadOnlyList<CardVisual> cards = _host.CurrentView().Cards;
        if (!string.IsNullOrEmpty(argument))
        {
            if (File.Exists(argument))
            {
                for (int i = 0; i < cards.Count; i++)
                    if (string.Equals(cards[i].Path, argument, StringComparison.OrdinalIgnoreCase))
                        return (argument, cards[i].Id, i);
                return (argument, Guid.Empty, 0);
            }
            if (Guid.TryParse(argument, out Guid id))
                for (int i = 0; i < cards.Count; i++)
                    if (cards[i].Id == id) return (cards[i].Path, id, i);
        }
        if (cards.Count == 0) return (null, Guid.Empty, 0);
        for (int i = 0; i < cards.Count; i++)
            if (cards[i].Id == _contextItemId) return (cards[i].Path, cards[i].Id, i);
        return (cards[0].Path, cards[0].Id, 0);
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

    private static string LabelFor(ContextAction action)
    {
        foreach ((ContextAction candidate, string label) in CardMenuItems)
            if (candidate == action) return label;
        return action.ToString();
    }

    private void ShowCardContextMenu(string? serialized)
    {
        if (_contextItemId == Guid.Empty) return;
        IntPtr menu = ShellNative.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        var actions = new Dictionary<int, ContextAction>();
        int id = 2000;

        // 门面在 ContextMenuRequested 时已按 G-11…G-18 算好条件显隐，
        // 这里只做 Win32 协议转换，不再自行硬编码 14 项。
        var items = new List<(ContextAction Action, string Label, bool Enabled, bool Separator)>();
        if (!string.IsNullOrEmpty(serialized))
        {
            foreach (string part in serialized.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = part.Split(':');
                if (fields.Length < 4 || !int.TryParse(fields[0], out int raw)) continue;
                if (!Enum.IsDefined(typeof(ContextAction), raw)) continue;
                items.Add(((ContextAction)raw, LabelFor((ContextAction)raw),
                    fields[2] == "1", fields[3] == "1"));
            }
        }
        if (items.Count == 0) // 参数缺失时回退，保证菜单永远可打开
            foreach ((ContextAction action, string label) in CardMenuItems)
                items.Add((action, label, true, action == ContextAction.TakeDownRight));

        foreach ((ContextAction action, string label, bool enabled, bool separator) in items)
        {
            if (separator)
                ShellNative.AppendMenuW(menu, ShellNative.MF_SEPARATOR, IntPtr.Zero, null);
            uint flags = ShellNative.MF_STRING;
            if (!enabled) flags |= ShellNative.MF_GRAYED | ShellNative.MF_DISABLED;
            actions[id] = action;
            ShellNative.AppendMenuW(menu, flags, new IntPtr(id), LabelWithAccessKey(label, CardAccessKey(action)));
            id++;
        }

        ShellNative.GetCursorPos(out ShellNative.POINT cursor);
        if (_menuAnchor is { } anchor) cursor = anchor;
        _menuAnchor = null;
        ShellNative.SetForegroundWindow(_host.Tray.HostHandle);
        try
        {
            int selected = ShellNative.TrackPopupMenuEx(menu,
                ShellNative.TPM_RETURNCMD | ShellNative.TPM_RIGHTBUTTON | ShellNative.TPM_NONOTIFY
                | ShellNative.TPM_WORKAREA,
                cursor.X, cursor.Y, _host.Tray.HostHandle, IntPtr.Zero);
            if (selected > 0 && actions.TryGetValue(selected, out ContextAction action))
            {
                _host.Facade.Handle(new ContextMenuItem(_contextItemId, action));
                _host.AdvanceFrame();
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
        _host.AdvanceFrame();
        _host.EnsurePresented();
        _host.RenderFrame();
        // 托盘单击 / 全局热键是「用户主动唤出」：这时给绳子键盘焦点（P0-6），
        // 之后 Tab / Enter / Shift+F10 才可用；悬停唤出仍然不抢焦点。
        if (_host.Facade.Current.IsRevealed) _host.Rope.FocusForKeyboard();
        else _host.Rope.ReleaseKeyboard();
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
        if (hwnd != IntPtr.Zero)
        {
            if (_hotKeyRegistered) ShellNative.UnregisterHotKey(hwnd, ToggleHotKeyId);
            ShellNative.UnregisterHotKey(hwnd, QuitHotKeyId);
        }

        _host.Tray.MenuRequested -= ShowTrayMenu;
        _host.Tray.LeftClicked -= ToggleLine;
        _host.Tray.ClipboardUpdated -= OnClipboardUpdated;
        _host.Tray.HotKeyPressed -= OnGlobalHotKey;
        _host.Tray.SessionEnding -= OnSessionEnding;
        _host.Tray.ThemeChanged -= OnSystemThemeChanged;
        _host.Rope.Mouse -= OnRopeMouse;
        _host.Rope.Key -= OnRopeKey;
        _host.Rope.Drag -= OnRopeDrag;
        _shortcutWindow?.Dispose();
        _shortcutWindow = null;
        foreach (FolderWatcher watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }

    // ---- 文件夹选择：现代 IFileOpenDialog + FOS_PICKFOLDERS（P1-12） ----

    private static string? PickFolder() => FolderPicker.Pick("选择要同时监视的文件夹");
}
