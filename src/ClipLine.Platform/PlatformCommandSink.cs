using ClipLine.Application;

namespace ClipLine.Platform;

/// <summary>
/// 产品侧的命令执行器：把门面发出的外部命令映射到 Win32 适配器。
/// 渲染类命令（PresentWindow / StartFlight / StartFall …）由壳层消费，
/// 这里显式记录为未处理，避免「假装在跑界面」。
/// </summary>
public sealed class PlatformCommandSink : ICommandSink
{
    private readonly Dictionary<string, FolderWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipboardListener _clipboard = new();
    private readonly TrayHostWindow _trayHost = new();
    private readonly List<string> _unhandled = new();
    private readonly List<string> _errors = new();

    public IReadOnlyList<string> Unhandled => _unhandled;
    public IReadOnlyList<string> Errors => _errors;

    public IntPtr HostWindow => _trayHost.Handle;

    /// <summary>可注入的状态文件路径：冒烟/测试用它隔离真实用户状态。</summary>
    public string StateFilePath { get; }

    /// <summary>可注入的桌面目录：菜单审计用临时目录，避免污染真实桌面。</summary>
    public string DesktopDirectory { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>审计模式：只判定命令是否被消费，不产生任何真实副作用。</summary>
    public bool DryRun { get; set; }

    /// <summary>本接收器真正实现的命令集合；与下方 switch 的 case 一一对应。</summary>
    private static readonly HashSet<CommandKind> Implemented = new()
    {
        CommandKind.WatchFolder,
        CommandKind.UnwatchFolder,
        CommandKind.RegisterClipboardListener,
        CommandKind.UnregisterClipboardListener,
        CommandKind.RegisterHotKey,
        CommandKind.UnregisterHotKey,
        CommandKind.RegisterTransientKey,
        CommandKind.UnregisterTransientKey,
        CommandKind.MoveToRecycleBin,
        CommandKind.PersistState,
        CommandKind.OpenFolder,
        CommandKind.OpenBrowser,
        CommandKind.OpenDefaultApp,
        CommandKind.ShowInExplorer,
        CommandKind.MoveFile,
        CommandKind.CopyFile,
        CommandKind.SetLaunchAtLogin,
        CommandKind.UpdateClipboardFileReference,
        CommandKind.RestoreSettings,
    };

    public static bool IsHandled(CommandKind kind) => Implemented.Contains(kind);

    public PlatformCommandSink(string? statePath = null) =>
        StateFilePath = statePath ?? StatePath;

    public static string OwnDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipLine", "Screenshots");

    public static string StatePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipLine", "state.json");

    private void EnsureHost() => _trayHost.Create();

    public void Execute(AppCommand command)
    {
        if (DryRun)
        {
            if (!IsHandled(command.Kind)) _unhandled.Add(command.Kind.ToString());
            return;
        }

        string argument = command.Argument ?? string.Empty;
        switch (command.Kind)
        {
            case CommandKind.WatchFolder:
                if (argument.Length == 0 || _watchers.ContainsKey(argument)) break;
                var watcher = new FolderWatcher();
                watcher.Start(argument);
                _watchers[argument] = watcher;
                break;

            case CommandKind.UnwatchFolder:
                if (_watchers.Remove(argument, out var existing)) existing.Dispose();
                break;

            case CommandKind.RegisterClipboardListener:
                EnsureHost();
                _clipboard.Attach(_trayHost.Handle);
                break;

            case CommandKind.UnregisterClipboardListener:
                if (_trayHost.Handle != IntPtr.Zero) _clipboard.Detach(_trayHost.Handle);
                break;

            case CommandKind.RegisterHotKey:
                EnsureHost();
                HotKey.Register(_trayHost.Handle, 1, argument);
                break;

            case CommandKind.UnregisterHotKey:
                if (_trayHost.Handle != IntPtr.Zero) HotKey.Unregister(_trayHost.Handle, 1);
                break;

            case CommandKind.RegisterTransientKey:
                EnsureHost();
                HotKey.Register(_trayHost.Handle, 2, argument);
                break;

            case CommandKind.UnregisterTransientKey:
                if (_trayHost.Handle != IntPtr.Zero) HotKey.Unregister(_trayHost.Handle, 2);
                break;

            case CommandKind.MoveToRecycleBin:
                RecycleBin.Move(argument);
                break;

            case CommandKind.PersistState:
                WriteAtomic(StateFilePath, argument);
                break;

            case CommandKind.OpenFolder:
            case CommandKind.OpenBrowser:
            case CommandKind.OpenDefaultApp:
                if (argument.Length > 0)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(argument)
                    {
                        UseShellExecute = true,
                    });
                }
                break;

            case CommandKind.ShowInExplorer:
                if (argument.Length > 0 && File.Exists(argument))
                {
                    System.Diagnostics.Process.Start(new                     System.Diagnostics.ProcessStartInfo("explorer.exe")
                    {
                        Arguments = "/select,\"" + argument + "\"",
                        UseShellExecute = true,
                    });
                }
                break;

            case CommandKind.MoveFile:
                Transfer(argument, move: true);
                break;

            case CommandKind.CopyFile:
                Transfer(argument, move: false);
                break;

            case CommandKind.SetLaunchAtLogin:
                SetLaunchAtLogin(argument == "true" || argument == "1");
                break;

            case CommandKind.UpdateClipboardFileReference:
                // D-08：v1 的剪贴板捕获走 CF_BITMAP/CF_DIB 位图，没有文件引用需要改写；
                // 这里显式消费该命令，避免落入 unhandled（编辑器迭代时再补真实实现）。
                break;

            case CommandKind.RestoreSettings:
                // v1 不借用系统临时设置（热键、DPI 等），退出时无需还原；
                // 显式消费，避免被误判为「无人处理」。
                break;

            default:
                // 渲染 / 托盘 / 音效 / 设置 / 编辑器命令由各自的平台层组件消费。
                _unhandled.Add(command.Kind.ToString());
                break;
        }
    }

    /// <summary>解析 "源->目标"，把 Desktop\ 占位符展开成真实桌面目录，并处理同名冲突。</summary>
    private void Transfer(string argument, bool move)
    {
        int split = argument.LastIndexOf("->", StringComparison.Ordinal);
        if (split <= 0 || split + 2 >= argument.Length)
        {
            _errors.Add($"{(move ? "MoveFile" : "CopyFile")}: 参数格式无效 {argument}");
            return;
        }

        string source = argument[..split];
        string destination = ResolveDesktop(argument[(split + 2)..]);
        try
        {
            if (!File.Exists(source))
            {
                _errors.Add($"{(move ? "MoveFile" : "CopyFile")}: 源文件不存在 {source}");
                return;
            }
            string? directory = System.IO.Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            destination = Unique(destination);
            if (move) File.Move(source, destination);
            else File.Copy(source, destination);
        }
        catch (Exception ex)
        {
            _errors.Add($"{(move ? "MoveFile" : "CopyFile")}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal string ResolveDesktop(string raw)
    {
        const string prefix = "Desktop\\";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return raw;
        return System.IO.Path.Combine(DesktopDirectory, raw[prefix.Length..]);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = System.IO.Path.GetDirectoryName(path) ?? ".";
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string extension = System.IO.Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = System.IO.Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return System.IO.Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>便携版用 HKCU Run 项；MSIX 版由系统「启动」设置管理 startupTask。</summary>
    private void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enabled)
            {
                string? exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe)) key.SetValue("ClipLine", "\"" + exe + "\"");
            }
            else key.DeleteValue("ClipLine", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            _errors.Add($"SetLaunchAtLogin: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>原子写：临时文件 + 替换，避免异常退出留下半截状态文件（A-05 / A-09）。</summary>
    internal static void WriteAtomic(string path, string content)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        _trayHost.Dispose();
    }
}
