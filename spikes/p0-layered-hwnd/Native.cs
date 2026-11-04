using System.Runtime.InteropServices;
using System.Text;

namespace ClipLine.P0Spike;

/// <summary>Win32 常量、结构与 P/Invoke 声明。仅用于 P0 验证切片。</summary>
internal static class Native
{
    internal const uint WS_EX_TRANSPARENT = 0x00000020;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_LAYERED = 0x00080000;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WS_EX_APPWINDOW = 0x00040000;
    internal const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    internal const uint WS_POPUP = 0x80000000;

    internal const int GWL_EXSTYLE = -20;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    internal const uint ULW_ALPHA = 0x00000002;
    internal const byte AC_SRC_OVER = 0;
    internal const byte AC_SRC_ALPHA = 1;
    internal const uint DIB_RGB_COLORS = 0;

    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_HOTKEY = 0x0312;
    internal const uint WM_APP = 0x8000;

    internal const uint NIM_ADD = 0;
    internal const uint NIM_DELETE = 1;
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2。</summary>
    internal static readonly IntPtr DpiPerMonitorV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int CX; public int CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int FIcon;
        public int XHotspot;
        public int YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint UID;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string SzTip;
        public uint DwState;
        public uint DwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string SzInfo;
        public uint UTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string SzInfoTitle;
        public uint DwInfoFlags;
        public Guid GuidItem;
        public IntPtr HBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszClassName;
        public IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr HWnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Pt;
        public uint LPrivate;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ---- user32 ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>系统 DPI；与 GetDpiForWindow 不同，它不要求有效 HWND。</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetThreadDpiAwarenessContext();

    /// <summary>返回 -1 无效 / 0 UNAWARE / 1 SYSTEM_AWARE / 2 PER_MONITOR_AWARE（PMv2 也是 2）。</summary>
    [DllImport("user32.dll")]
    internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    internal static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    internal static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    // ---- gdi32 ----
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

    // ---- shell32 ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    // ---- kernel32 ----
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
