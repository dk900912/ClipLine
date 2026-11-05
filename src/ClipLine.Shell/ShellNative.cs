using System.Runtime.InteropServices;
using System.Text;

namespace ClipLine.Shell;

/// <summary>
/// 渲染壳 v0 的 Win32 P/Invoke 声明。窗口机制已由 P0 切片（spikes/p0-layered-hwnd）验证：
/// 分层 HWND + GDI 32bpp DIB 上屏，不带 WS_EX_NOREDIRECTIONBITMAP（该样式属于将来的 Win2D/Composition 路径）。
/// </summary>
internal static class ShellNative
{
    internal const uint WS_EX_TRANSPARENT = 0x00000020;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_LAYERED = 0x00080000;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WS_EX_APPWINDOW = 0x00040000;
    internal const uint WS_POPUP = 0x80000000;

    internal const int GWL_EXSTYLE = -20;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal static readonly IntPtr HwndTopmost = new(-1);
    internal static readonly IntPtr HwndMessage = new(-3);

    internal const uint ULW_ALPHA = 0x00000002;
    internal const byte AcSrcOver = 0;
    internal const byte AcSrcAlpha = 1;
    internal const uint DibRgbColors = 0;

    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_SETCURSOR = 0x0020;
    internal const uint WM_APP = 0x8000;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_LBUTTONUP = 0x0202;

    // 分层窗口必须自带光标：hCursor 为 NULL 时 DefWindowProc 不重置光标，
    // 系统遗留的 IDC_APPSTARTING / IDC_WAIT 会一直跟着鼠标（v1 实测的「加载小圆圈」）。
    internal const uint HTCLIENT = 1;
    internal static readonly IntPtr IdcArrow = new(32512);
    internal static readonly IntPtr IdcHand = new(32649);
    internal static readonly IntPtr IdcWait = new(32514);
    internal static readonly IntPtr IdcAppStarting = new(32650);

    internal const uint NIM_ADD = 0;
    internal const uint NIM_MODIFY = 1;
    internal const uint NIM_DELETE = 2;
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_INFO = 0x00000010;

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const uint WM_TIMER = 0x0113;
    internal const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    internal const uint SPI_GETHIGHCONTRAST = 0x0042;
    internal const uint SPI_GETWORKAREA = 0x0030;
    internal const uint HCF_HIGHCONTRASTON = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int CX; public int CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

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
    internal struct HIGHCONTRASTW
    {
        public uint CbSize;
        public uint DwFlags;
        public IntPtr LpszDefaultScheme;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ---- user32 ----
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

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

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref HIGHCONTRASTW pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

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

    // 光标：窗口类默认光标 + WM_SETCURSOR 显式设置，防止沿用系统等待光标。
    [DllImport("user32.dll", EntryPoint = "LoadCursorW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr hCursor);

    // 只读单个指针值，避免把 WNDCLASSEXW 作为 ref 传出（系统会回写内部指针，
    // 托管封送器误释放会直接造成 0xC0000374 堆损坏）。
    internal const int GCLP_HCURSOR = -12;

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int nIndex);

    // 卡片「已复制」角标：GDI 文本蒙版（中文，无需额外字体库）。
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    internal static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", EntryPoint = "TextOutW", CharSet = CharSet.Unicode)]
    internal static extern bool TextOutW(IntPtr hdc, int x, int y, string lpString, int c);

    // ---- kernel32 ----
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AttachConsole(uint dwProcessId);

    internal const uint AttachParentProcess = 0xFFFFFFFF;

    // ============================================================
    // v1 真实输入链路：鼠标 / 剪贴板 / 托盘菜单 / 热键
    // ============================================================

    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_CLIPBOARDUPDATE = 0x031D;
    internal const uint WM_HOTKEY = 0x0312;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_ENDSESSION = 0x0016;
    internal const uint WM_QUERYENDSESSION = 0x0011;
    internal const uint WM_USER_TRAY = WM_APP + 1;

    internal const uint CF_BITMAP = 2;
    internal const uint CF_DIB = 8;
    internal const uint CF_HDROP = 15;

    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_GRAYED = 0x00000001;
    internal const uint MF_DISABLED = 0x00000002;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_POPUP = 0x00000010;
    internal const uint MF_CHECKED = 0x00000008;

    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_NONOTIFY = 0x0080;
    internal const uint TPM_LEFTALIGN = 0x0000;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y,
        IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MessageBeep(uint uType);

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpOperation, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    internal const int SwShowNormal = 1;
    internal const uint MbOk = 0;

    // ============================================================
    // v1.4：自绘替身窗口 + 真实鼠标点击端到端验证
    // ============================================================

    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_CLOSE = 0x0010;
    internal const int SW_SHOW = 5;

    // ---- v1.6：原生窗口行为 / 主题热切换 / 高 DPI ----
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_THEMECHANGED = 0x031A;
    internal const uint WM_SETFOCUS = 0x0007;
    internal const uint WM_KILLFOCUS = 0x0008;
    internal const uint WM_CONTEXTMENU = 0x007B;
    internal const int EventObjectNameChange = 0x800C;
    internal const int ObjidWindow = 0;
    internal const int SwpNoZorder = 0x0004;
    internal const uint WM_DPICHANGED = 0x02E0;
    internal const uint WM_MOUSELEAVE = 0x02A3;
    internal const uint WM_NCHITTEST = 0x0084;
    internal const uint WM_GETOBJECT = 0x003C;
    internal const uint WM_MENUCHAR = 0x0120;
    internal const int HtCaption = 2;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;
    internal const uint TmeLeave = 0x00000002;
    internal const uint TPM_WORKAREA = 0x10000;
    internal const int SmCxDoubleClk = 36;
    internal const int SmCyDoubleClk = 37;

    internal const int ColorWindow = 5;
    internal const int ColorWindowText = 8;
    internal const int ColorHighlight = 13;
    internal const int ColorHighlightText = 14;
    internal const int ColorBtnFace = 15;
    internal const int ColorBtnShadow = 16;
    internal const int ColorGrayText = 17;
    internal const int ColorWindowFrame = 6;
    internal const int ColorBtnHighlight = 20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public uint CbSize;
        public uint DwFlags;
        public IntPtr HwndTrack;
        public uint DwHoverTime;
    }

    internal enum QUERY_USER_NOTIFICATION_STATE
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("user32.dll")]
    internal static extern int GetSysColor(int nIndex);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);

    [DllImport("user32.dll", EntryPoint = "GetTextExtentPoint32W", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c,
        out SIZE psizl);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int pvAttribute, int cbAttribute);

    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwcpRound = 2;

    internal const byte VkEscape = 0x1B;
    internal const byte VkSpace = 0x20;
    internal const byte VkLeft = 0x25;
    internal const byte VkRight = 0x27;
    internal const byte VkC = 0x43;
    internal const byte VkS = 0x53;
    internal const byte VkQ = 0x51;
    internal const byte VkY = 0x59;
    internal const byte VkZ = 0x5A;
    internal const byte VkDelete = 0x2E;
    internal const byte VkTab = 0x09;
    internal const byte VkEnter = 0x0D;
    internal const byte VkUp = 0x26;
    internal const byte VkDown = 0x28;
    internal const byte VkF10 = 0x79;
    internal const byte VkShift = 0x10;
    internal const byte VkControl = 0x11;
    internal const byte VkMenu = 0x12;
    internal const byte VkLWin = 0x5B;
    internal const byte VkApps = 0x5D;
    internal const uint MkLButton = 0x0001;
    internal const uint KeyeventfKeyup = 0x0002;

    internal const uint MouseeventfLeftdown = 0x0002;
    internal const uint MouseeventfLeftup = 0x0004;
    internal const uint MouseeventfRightdown = 0x0008;
    internal const uint MouseeventfRightup = 0x0010;

    internal const uint MnGetHMenu = 0x01E1;
    internal const uint MfByPosition = 0x0400;
    internal const int SmCyMenu = 55;
    internal const int HtClient = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    /// <summary>无障碍事件：窗口名变化后让 Narrator 重新朗读（P0-6）。</summary>
    [DllImport("user32.dll")]
    internal static extern void NotifyWinEvent(int eventId, IntPtr hWnd, int idObject, int idChild);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    internal static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMenuStringW", CharSet = CharSet.Unicode)]
    internal static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem,
        System.Text.StringBuilder lpString, int cchMax, uint uFlag);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMenuItemRect(IntPtr hWnd, IntPtr hMenu, uint uItem, out RECT lprcItem);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
}
