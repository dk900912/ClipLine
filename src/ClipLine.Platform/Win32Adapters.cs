using System.ComponentModel;
using System.Runtime.InteropServices;
using ClipLine.Domain;

namespace ClipLine.Platform;

public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>N-02 / docs/03 §1：主屏 rcWork + 单一系统缩放（v1 不枚举显示器）。</summary>
public static class DisplayInfo
{
    public static double ScaleFromDpi(uint dpi) => dpi == 0 ? 1.0 : dpi / 96.0;

    public static double SystemScale => ScaleFromDpi(Win32Native.GetDpiForSystem());

    public static WorkArea GetWorkArea()
    {
        var rect = default(Win32Native.RECT);
        if (!Win32Native.SystemParametersInfoW(Win32Native.SPI_GETWORKAREA, 0, ref rect, 0))
        {
            return new WorkArea(0, 0, Win32Native.GetSystemMetrics(0), Win32Native.GetSystemMetrics(1));
        }
        return new WorkArea(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static int PrimaryWidthDip() => (int)Math.Round(GetWorkArea().Width / SystemScale);

    /// <summary>顶部热条 8–12 DIP，取中值 10（S-02）。</summary>
    public static int RevealBandDip => Layout.RevealBandDip;
}

public enum FolderChange
{
    Added,
    Removed,
    Modified,
    RenamedNewName,
    Unknown,
}

/// <summary>
/// F-05：ReadDirectoryChangesW + 0.2 秒防抖。
/// v1 用同步阻塞读取（句柄不带 FILE_FLAG_OVERLAPPED）：后台线程零轮询地等事件，
/// 关闭句柄即可让阻塞调用返回。解析规则与产品一致，Parse 是纯函数可单测。
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    public const int BufferSize = 64 * 1024;
    public const long DebounceMs = 200;

    private IntPtr _handle = IntPtr.Zero;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string, FolderChange, long>? Changed;

    public static bool ShouldSuppress(long lastEventMs, long nowMs) => nowMs - lastEventMs < DebounceMs;

    public static FolderChange ToChange(uint action) => action switch
    {
        Win32Native.FILE_ACTION_ADDED => FolderChange.Added,
        Win32Native.FILE_ACTION_REMOVED => FolderChange.Removed,
        Win32Native.FILE_ACTION_MODIFIED => FolderChange.Modified,
        Win32Native.FILE_ACTION_RENAMED_NEW_NAME => FolderChange.RenamedNewName,
        _ => FolderChange.Unknown,
    };

    /// <summary>解析 FILE_NOTIFY_INFORMATION 链：NextEntryOffset / Action / FileNameLength / UTF-16 名称。</summary>
    public static IReadOnlyList<(string Name, FolderChange Change)> Parse(byte[] buffer, int byteCount)
    {
        var result = new List<(string, FolderChange)>();
        int offset = 0;
        while (offset + 12 <= byteCount)
        {
            uint next = BitConverter.ToUInt32(buffer, offset);
            uint action = BitConverter.ToUInt32(buffer, offset + 4);
            int length = (int)BitConverter.ToUInt32(buffer, offset + 8);
            if (length < 0 || offset + 12 + length > byteCount) break;
            string name = Win32Native.DecodeNotifyFileName(buffer, offset + 12, length);
            result.Add((name, ToChange(action)));
            if (next == 0) break;
            offset += (int)next;
        }
        return result;
    }

    public void Start(string path)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _handle = Win32Native.CreateFileW(
            path,
            Win32Native.FILE_LIST_DIRECTORY,
            Win32Native.FILE_SHARE_READ | Win32Native.FILE_SHARE_WRITE | Win32Native.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32Native.OPEN_EXISTING,
            Win32Native.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        if (_handle == IntPtr.Zero || _handle == Win32Native.INVALID_HANDLE_VALUE)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法监视目录：{path}");

        var token = _cts.Token;
        _pump = Task.Run(() => Pump(path, token), token);
    }

    private void Pump(string path, CancellationToken token)
    {
        byte[] buffer = new byte[BufferSize];
        while (!token.IsCancellationRequested)
        {
            uint notifyFilter = Win32Native.FILE_NOTIFY_CHANGE_FILE_NAME |
                                Win32Native.FILE_NOTIFY_CHANGE_LAST_WRITE |
                                Win32Native.FILE_NOTIFY_CHANGE_CREATION;
            if (!Win32Native.ReadDirectoryChangesW(
                    _handle, buffer, (uint)buffer.Length, false, notifyFilter,
                    out uint returned, IntPtr.Zero, IntPtr.Zero)) break;
            if (returned == 0) continue;

            long now = Environment.TickCount64;
            foreach (var (name, change) in Parse(buffer, (int)returned))
            {
                string full = System.IO.Path.Combine(path, name);
                if (_lastSeen.TryGetValue(full, out long last) && ShouldSuppress(last, now)) continue;
                _lastSeen[full] = now;
                Changed?.Invoke(full, change, now);
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        IntPtr handle = _handle;
        if (handle != IntPtr.Zero && handle != Win32Native.INVALID_HANDLE_VALUE)
        {
            // 同步 ReadDirectoryChangesW 正阻塞在后台线程；直接 CloseHandle 会
            // 等挂起 I/O 完成而死锁（冒烟退出时实测）。先取消 I/O，再关句柄。
            _handle = IntPtr.Zero;
            Win32Native.CancelIoEx(handle, IntPtr.Zero);
            Task? pump = _pump;
            if (pump is not null)
            {
                try { pump.Wait(TimeSpan.FromMilliseconds(500)); }
                catch (AggregateException) { /* 取消导致的任务异常无需上抛 */ }
            }
            Win32Native.CloseHandle(handle);
        }
        _pump = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}

/// <summary>docs/03 §10：消息驱动剪贴板，替代 macOS 的 0.5 秒轮询（空闲零开销）。</summary>
public sealed class ClipboardListener
{
    public const uint WmClipboardUpdate = Win32Native.WM_CLIPBOARDUPDATE;

    public bool Attach(IntPtr hostWindow) => Win32Native.AddClipboardFormatListener(hostWindow);

    public bool Detach(IntPtr hostWindow) => Win32Native.RemoveClipboardFormatListener(hostWindow);
}

public static class HotKey
{
    public const uint WmHotKey = Win32Native.WM_HOTKEY;

    public static uint ParseModifiers(string shortcut)
    {
        uint modifiers = 0;
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= Win32Native.MOD_CONTROL;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= Win32Native.MOD_ALT;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= Win32Native.MOD_SHIFT;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= Win32Native.MOD_WIN;
        }
        return modifiers;
    }

    public static bool Register(IntPtr hostWindow, int id, string shortcut)
    {
        string key = shortcut.Split('+', StringSplitOptions.TrimEntries)[^1];
        if (key.Length != 1) return false;
        return Win32Native.RegisterHotKey(hostWindow, id, ParseModifiers(shortcut), char.ToUpperInvariant(key[0]));
    }

    public static bool Unregister(IntPtr hostWindow, int id) => Win32Native.UnregisterHotKey(hostWindow, id);
}

/// <summary>N-08：IFileOperation 不返回回收站路径，撤销靠 ClipLine 自有软删除索引。</summary>
public static class RecycleBin
{
    public static bool Move(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var operation = new Win32Native.SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = Win32Native.FO_DELETE,
            pFrom = path + "\0",
            pTo = string.Empty,
            fFlags = Win32Native.FOF_ALLOWUNDO | Win32Native.FOF_NOCONFIRMATION |
                     Win32Native.FOF_SILENT | Win32Native.FOF_NOERRORUI,
        };
        return Win32Native.SHFileOperationW(ref operation) == 0 && !operation.fAnyOperationsAborted;
    }
}

/// <summary>
/// P0 结论：托盘图标必须挂在独立 message-only 宿主窗口，
/// 不能挂在会于收起 0.3 秒后销毁的瞬时线窗口。
/// </summary>
public sealed class TrayHostWindow : IDisposable
{
    public IntPtr Handle { get; private set; }

    public void Create()
    {
        if (Handle != IntPtr.Zero) return;
        Handle = Win32Native.CreateWindowExW(
            0, "STATIC", "ClipLineTrayHost", 0, 0, 0, 0, 0,
            Win32Native.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Win32Native.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }
}
