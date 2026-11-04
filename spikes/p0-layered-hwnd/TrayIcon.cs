using System.Runtime.InteropServices;

namespace ClipLine.P0Spike;

/// <summary>托盘图标占位：用 Win32 直接画 16×16 的 E「双线」图案，不依赖 SVG 栅格化工具。</summary>
internal static unsafe class TrayIcon
{
    internal const uint CallbackMessage = Native.WM_APP + 1;
    private const int Size = 16;

    private static uint Bgra(int r, int g, int b, int a = 255) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | (uint)r;

    internal static IntPtr CreateDoubleLineIcon()
    {
        var header = new Native.BITMAPINFOHEADER
        {
            BiSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            BiWidth = Size,
            BiHeight = -Size,
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = 0,
        };

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr color = Native.CreateDIBSection(memDc, ref header, Native.DIB_RGB_COLORS,
            out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero)
            throw new InvalidOperationException("无法创建托盘图标位图。");

        uint* pixels = (uint*)bits.ToPointer();
        new Span<uint>(pixels, Size * Size).Clear();

        // 上线：靛蓝直线
        for (int x = 2; x <= 13; x++)
        {
            Put(pixels, x, 4, Bgra(79, 91, 213));
            Put(pixels, x, 5, Bgra(79, 91, 213));
        }
        // 下线：同曲线略下垂，紫色
        for (int x = 2; x <= 13; x++)
        {
            int sag = (int)Math.Round(2 * Math.Sin(Math.PI * (x - 2) / 11.0));
            Put(pixels, x, 10 + sag, Bgra(108, 99, 244));
            Put(pixels, x, 11 + sag, Bgra(108, 99, 244));
        }
        // 中间夹子：暖橙
        for (int y = 6; y <= 9; y++)
            for (int x = 7; x <= 8; x++) Put(pixels, x, y, Bgra(239, 138, 36));

        // 全 0 的 1bpp AND mask = 图标完全不透明，由 32bpp alpha 决定形状。
        // CreateBitmap(lpBits=NULL) 内容未定义，必须显式给一块清零缓冲。
        int maskBytes = (Size / 8) * Size; // 16bpp 行 = 2 字节/行 × 16 行
        IntPtr maskBits = Marshal.AllocHGlobal(maskBytes);
        Marshal.Copy(new byte[maskBytes], 0, maskBits, maskBytes);
        IntPtr mask = Native.CreateBitmap(Size, Size, 1, 1, maskBits);
        var info = new Native.ICONINFO
        {
            FIcon = 1,
            XHotspot = 0,
            YHotspot = 0,
            HbmMask = mask,
            HbmColor = color,
        };
        IntPtr icon = Native.CreateIconIndirect(ref info);

        Marshal.FreeHGlobal(maskBits);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        return icon;
    }

    private static void Put(uint* pixels, int x, int y, uint value)
    {
        if ((uint)x >= Size || (uint)y >= Size) return;
        pixels[y * Size + x] = value;
    }

    internal static bool Add(IntPtr hWnd, IntPtr hIcon, string tip)
    {
        var data = new Native.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            HWnd = hWnd,
            UID = 1,
            UFlags = Native.NIF_ICON | Native.NIF_MESSAGE | Native.NIF_TIP,
            UCallbackMessage = CallbackMessage,
            HIcon = hIcon,
            SzTip = tip,
            SzInfo = string.Empty,
            SzInfoTitle = string.Empty,
        };
        return Native.Shell_NotifyIconW(Native.NIM_ADD, ref data);
    }

    internal static bool Remove(IntPtr hWnd)
    {
        var data = new Native.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            HWnd = hWnd,
            UID = 1,
            SzTip = string.Empty,
            SzInfo = string.Empty,
            SzInfoTitle = string.Empty,
        };
        return Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
    }
}
