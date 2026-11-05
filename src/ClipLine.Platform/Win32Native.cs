using System.Runtime.InteropServices;
using System.Text;

namespace ClipLine.Platform;

/// <summary>
/// 手写 P/Invoke 的最小集合。产品正式版按定稿改用 CsWin32 源生成器；
/// 这里保持零 NuGet 依赖，便于在没有 WinUI 3 工作负载的本机编译验证。
/// </summary>
internal static class Win32Native
{
    internal const uint FILE_LIST_DIRECTORY = 0x0001;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint FILE_SHARE_DELETE = 0x00000004;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    internal const uint FILE_NOTIFY_CHANGE_FILE_NAME = 0x0001;
    internal const uint FILE_NOTIFY_CHANGE_DIR_NAME = 0x0002;
    internal const uint FILE_NOTIFY_CHANGE_LAST_WRITE = 0x0010;
    internal const uint FILE_NOTIFY_CHANGE_CREATION = 0x0040;

    internal const uint FILE_ACTION_ADDED = 0x1;
    internal const uint FILE_ACTION_REMOVED = 0x2;
    internal const uint FILE_ACTION_MODIFIED = 0x3;
    internal const uint FILE_ACTION_RENAMED_OLD_NAME = 0x4;
    internal const uint FILE_ACTION_RENAMED_NEW_NAME = 0x5;

    internal const uint SPI_GETWORKAREA = 0x0030;
    internal const uint WM_CLIPBOARDUPDATE = 0x031D;
    internal const uint WM_HOTKEY = 0x0312;

    internal const uint FO_DELETE = 0x0003;
    internal const ushort FOF_SILENT = 0x0004;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOERRORUI = 0x0400;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;

    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    internal struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadDirectoryChangesW(
        IntPtr hDirectory, [Out] byte[] lpBuffer, uint nBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter,
        out uint lpBytesReturned, IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>取消句柄上所有挂起的 I/O；用于让同步阻塞的 ReadDirectoryChangesW 立即返回。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>FILE_NOTIFY_INFORMATION：UTF-16LE 文件名解码（纯函数，便于测试）。</summary>
    internal static string DecodeNotifyFileName(byte[] buffer, int offset, int byteLength) =>
        byteLength <= 0 ? string.Empty : Encoding.Unicode.GetString(buffer, offset, byteLength);
}
