using System.Runtime.InteropServices;

namespace ClipLine.Shell;

/// <summary>
/// 真实图片解码（v1）：直接 P/Invoke 系统自带 gdiplus.dll，零 NuGet 依赖。
/// 负责两件事：① 把文件按「等比 contain」缩放到卡片内区，读回 32bpp BGRA 像素；
/// ② 把剪贴板 CF_BITMAP 编码成 PNG 落到 ClipLine 自有目录。
/// GDI+ 在 Windows 8.1+ 支持 PNG / JPEG / GIF / BMP / TIFF；HEIC 需系统可选解码器，
/// 缺失时 <see cref="LoadScaled"/> 返回 null，渲染层回退到占位色，不让应用崩掉。
/// </summary>
internal static class GdiPlus
{
    private const int PixelFormat32bppArgb = 0x26200A;
    private const int InterpolationHighQualityBicubic = 7;
    private const uint ImageLockModeRead = 1;
    private const uint ImageLockModeWrite = 2;

    private static readonly Guid PngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");
    private static readonly object Sync = new();
    private static IntPtr s_token;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint Version;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupOutput
    {
        public IntPtr NotificationHook;
        public IntPtr NotificationUnhook;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpRect
    {
        public int X, Y, Width, Height;
        public GpRect(int x, int y, int w, int h) { X = x; Y = y; Width = w; Height = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapData
    {
        public uint Width;
        public uint Height;
        public int Stride;
        public int PixelFormat;
        public IntPtr Scan0;
        public IntPtr Reserved;
    }

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, out StartupOutput output);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr hpal, out IntPtr bitmap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride,
        int format, IntPtr scan0, out IntPtr bitmap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageWidth(IntPtr image, out uint width);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageHeight(IntPtr image, out uint height);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGraphicsClear(IntPtr graphics, uint argb);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipBitmapLockBits(IntPtr bitmap, ref GpRect rect, uint flags,
        int format, ref BitmapData data);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref BitmapData data);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateHBITMAPFromBitmap(IntPtr bitmap, out IntPtr hbmReturn, uint background);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipSaveImageToFile(IntPtr image, string filename,
        ref Guid encoder, IntPtr parameters);

    private static void EnsureStarted()
    {
        if (s_token != IntPtr.Zero) return;
        lock (Sync)
        {
            if (s_token != IntPtr.Zero) return;
            var input = new StartupInput { Version = 1 };
            if (GdiplusStartup(out IntPtr token, ref input, out _) != 0 || token == IntPtr.Zero)
                throw new InvalidOperationException("GDI+ 初始化失败");
            s_token = token;
        }
    }

    /// <summary>返回 w*h*4 的 BGRA（未预乘）像素；失败返回 null。</summary>
    internal static byte[]? LoadScaled(string path, int width, int height)
    {
        if (width <= 0 || height <= 0 || string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        EnsureStarted();
        IntPtr source = IntPtr.Zero, target = IntPtr.Zero, graphics = IntPtr.Zero;
        try
        {
            if (GdipCreateBitmapFromFile(path, out source) != 0 || source == IntPtr.Zero) return null;
            if (GdipGetImageWidth(source, out uint sw) != 0 || GdipGetImageHeight(source, out uint sh) != 0
                || sw == 0 || sh == 0) return null;

            double ratio = Math.Min(width / (double)sw, height / (double)sh);
            int drawW = Math.Max(1, (int)Math.Round(sw * ratio));
            int drawH = Math.Max(1, (int)Math.Round(sh * ratio));
            int drawX = (width - drawW) / 2, drawY = (height - drawH) / 2;

            if (GdipCreateBitmapFromScan0(width, height, width * 4, PixelFormat32bppArgb,
                    IntPtr.Zero, out target) != 0 || target == IntPtr.Zero) return null;
            if (GdipGetImageGraphicsContext(target, out graphics) != 0 || graphics == IntPtr.Zero) return null;
            GdipSetInterpolationMode(graphics, InterpolationHighQualityBicubic);
            GdipGraphicsClear(graphics, 0x00000000u); // 全透明底
            if (GdipDrawImageRectI(graphics, source, drawX, drawY, drawW, drawH) != 0) return null;

            var rect = new GpRect(0, 0, width, height);
            var data = default(BitmapData);
            if (GdipBitmapLockBits(target, ref rect, ImageLockModeRead, PixelFormat32bppArgb, ref data) != 0
                || data.Scan0 == IntPtr.Zero) return null;
            try
            {
                byte[] pixels = new byte[width * height * 4];
                int stride = data.Stride;
                for (int y = 0; y < height; y++)
                    Marshal.Copy(data.Scan0 + y * stride, pixels, y * width * 4, width * 4);
                return pixels;
            }
            finally { GdipBitmapUnlockBits(target, ref data); }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (Exception) { return null; }
        finally
        {
            if (graphics != IntPtr.Zero) GdipDeleteGraphics(graphics);
            if (target != IntPtr.Zero) GdipDisposeImage(target);
            if (source != IntPtr.Zero) GdipDisposeImage(source);
        }
    }

    /// <summary>把剪贴板 HBITMAP 存成 PNG；返回是否成功。</summary>
    internal static bool SaveBitmapAsPng(IntPtr hBitmap, string path)
    {
        if (hBitmap == IntPtr.Zero) return false;
        EnsureStarted();
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (GdipCreateBitmapFromHBITMAP(hBitmap, IntPtr.Zero, out bitmap) != 0 || bitmap == IntPtr.Zero)
                return false;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Guid encoder = PngEncoder;
            int status = GdipSaveImageToFile(bitmap, path, ref encoder, IntPtr.Zero);
            return status == 0 && File.Exists(path);
        }
        catch (Exception) { return false; }
        finally { if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap); }
    }

    /// <summary>
    /// CF_DIB（BITMAPINFOHEADER + 像素）→ PNG。部分截图工具只提供 DIB 不提供 HBITMAP，
    /// 因此这条路径是「挂上复制的图片」能覆盖 Win+Shift+S 的关键。
    /// 只处理 24/32bpp 非压缩位图，其余交给渲染层回退。
    /// </summary>
    internal static bool SaveDibAsPng(IntPtr pDib, string path)
    {
        if (pDib == IntPtr.Zero) return false;
        EnsureStarted();
        IntPtr unmanaged = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            var header = Marshal.PtrToStructure<ShellNative.BITMAPINFOHEADER>(pDib);
            int width = header.BiWidth;
            int signedHeight = header.BiHeight;
            int bpp = header.BiBitCount;
            if (width <= 0 || signedHeight == 0 || header.BiSize < 40) return false;
            if (bpp != 24 && bpp != 32) return false;
            if (header.BiCompression != 0 && header.BiCompression != 3) return false;

            int height = Math.Abs(signedHeight);
            bool bottomUp = signedHeight > 0;
            int stride = ((width * bpp + 31) / 32) * 4;
            int palette = bpp <= 8 ? 1 << bpp : 0;
            if (header.BiClrUsed != 0 && bpp <= 8) palette = (int)header.BiClrUsed;
            int masks = header.BiCompression == 3 ? 12 : 0;
            IntPtr scan0 = IntPtr.Add(pDib, (int)header.BiSize + palette * 4 + masks);

            byte[] buffer = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int src = bottomUp ? height - 1 - y : y;
                Marshal.Copy(IntPtr.Add(scan0, src * stride), buffer, y * stride, stride);
            }
            unmanaged = Marshal.AllocHGlobal(buffer.Length);
            Marshal.Copy(buffer, 0, unmanaged, buffer.Length);

            int format = bpp == 32 ? 0x262009 : 0x262008; // 32bppRGB / 24bppRGB
            if (GdipCreateBitmapFromScan0(width, height, stride, format, unmanaged, out bitmap) != 0
                || bitmap == IntPtr.Zero) return false;

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Guid encoder = PngEncoder;
            return GdipSaveImageToFile(bitmap, path, ref encoder, IntPtr.Zero) == 0 && File.Exists(path);
        }
        catch (Exception) { return false; }
        finally
        {
            if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap);
            if (unmanaged != IntPtr.Zero) Marshal.FreeHGlobal(unmanaged);
        }
    }

    /// <summary>
    /// 把渲染壳的 32bpp 预乘 alpha DIB 直接存成 PNG。
    /// 这是不依赖桌面抓屏的确定性渲染证据（分层窗口常被别的顶层浮层遮挡）。
    /// </summary>
    internal static bool SavePremultipliedFrame(IntPtr scan0, int width, int height, string path)
    {
        if (scan0 == IntPtr.Zero || width <= 0 || height <= 0) return false;
        byte[] bgra = new byte[width * height * 4];
        Marshal.Copy(scan0, bgra, 0, bgra.Length);
        // DIB 是预乘 alpha（BGRA 已乘 a/255），PNG 需要非预乘。
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 0) { bgra[i] = bgra[i + 1] = bgra[i + 2] = 0; continue; }
            bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
        }
        return SaveBgraAsPng(bgra, width, height, path);
    }

    /// <summary>
    /// 从托管 BGRA 像素写 PNG。GDI+ 自己分配位图内存（scan0 = NULL），
    /// 再用 LockBits 写入像素：外部缓冲区直接喂给 PNG 编码器在本机不可靠。
    /// </summary>
    internal static bool SaveBgraAsPng(byte[] bgra, int width, int height, string path)
    {
        if (bgra.Length < width * height * 4) return false;
        EnsureStarted();
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (GdipCreateBitmapFromScan0(width, height, width * 4, PixelFormat32bppArgb,
                    IntPtr.Zero, out bitmap) != 0 || bitmap == IntPtr.Zero) return false;
            var rect = new GpRect(0, 0, width, height);
            var data = default(BitmapData);
            if (GdipBitmapLockBits(bitmap, ref rect, ImageLockModeWrite,
                    PixelFormat32bppArgb, ref data) != 0 || data.Scan0 == IntPtr.Zero) return false;
            try
            {
                int rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                    Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
            finally { GdipBitmapUnlockBits(bitmap, ref data); }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Guid encoder = PngEncoder;
            return GdipSaveImageToFile(bitmap, path, ref encoder, IntPtr.Zero) == 0 && File.Exists(path);
        }
        catch (Exception) { return false; }
        finally { if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap); }
    }

    /// <summary>右键「复制」：把图片文件以 CF_BITMAP 放回系统剪贴板。</summary>
    internal static bool CopyFileToClipboard(string path)
    {
        if (!File.Exists(path)) return false;
        EnsureStarted();
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (GdipCreateBitmapFromFile(path, out bitmap) != 0 || bitmap == IntPtr.Zero) return false;
            if (GdipCreateHBITMAPFromBitmap(bitmap, out IntPtr hBitmap, 0) != 0 || hBitmap == IntPtr.Zero)
                return false;
            if (!ShellNative.OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                ShellNative.EmptyClipboard();
                // 成功后句柄所有权移交系统，不能再 DeleteObject。
                return ShellNative.SetClipboardData(ShellNative.CF_BITMAP, hBitmap) != IntPtr.Zero;
            }
            finally { ShellNative.CloseClipboard(); }
        }
        catch (Exception) { return false; }
        finally { if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap); }
    }
}

/// <summary>
/// D-10：每张卡片只在内存里留一张缩略图；按路径 + 写入时间 + 尺寸做键，最多 48 张。
/// 避免 30 fps 帧驱动反复解码同一张图。
/// </summary>
internal sealed class ThumbnailCache
{
    private readonly Dictionary<string, byte[]?> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private const int Capacity = 48;

    internal byte[]? Get(string path, int width, int height)
    {
        long ticks;
        try { ticks = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { ticks = 0; }
        string key = $"{path}|{width}x{height}|{ticks}";
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out byte[]? cached)) return cached;
        }

        byte[]? pixels = GdiPlus.LoadScaled(path, width, height);
        lock (_entries)
        {
            if (!_entries.ContainsKey(key))
            {
                _entries[key] = pixels;
                _order.Enqueue(key);
                while (_order.Count > Capacity && _order.Count > 0)
                    _entries.Remove(_order.Dequeue());
            }
        }
        return pixels;
    }

    internal void Clear()
    {
        lock (_entries) { _entries.Clear(); _order.Clear(); }
    }
}
