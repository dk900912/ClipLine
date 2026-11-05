using ClipLine.Application;

namespace ClipLine.Platform;

/// <summary>
/// 产品侧的命令执行器：把门面发出的外部命令映射到 Win32 适配器。
/// 渲染类命令（PresentWindow / StartFlight / StartFall …）由 WinUI 3 + Win2D 层消费，
/// 这里显式记录为未处理，避免「假装在跑界面」。
/// </summary>
public sealed class PlatformCommandSink : ICommandSink
{
    private readonly Dictionary<string, FolderWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipboardListener _clipboard = new();
    private readonly TrayHostWindow _trayHost = new();
    private readonly List<string> _unhandled = new();

    public IReadOnlyList<string> Unhandled => _unhandled;

    public IntPtr HostWindow => _trayHost.Handle;

    /// <summary>可注入的状态文件路径：冒烟/测试用它隔离真实用户状态。</summary>
    public string StateFilePath { get; }

    public PlatformCommandSink(string? statePath = null) =>
        StateFilePath = statePath ?? StatePath;

    public static string OwnDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipLine", "Screenshots");

    public static string StatePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipLine", "state.json");

    private void EnsureHost() => _trayHost.Create();

    public void Execute(AppCommand command)
    {
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
            case CommandKind.ShowInExplorer:
                if (argument.Length > 0)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(argument)
                    {
                        UseShellExecute = true,
                    });
                }
                break;

            default:
                // 渲染 / 托盘 / 音效 / 设置 / 编辑器命令由各自的平台层组件消费。
                _unhandled.Add(command.Kind.ToString());
                break;
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
