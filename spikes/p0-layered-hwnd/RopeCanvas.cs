using System.Runtime.InteropServices;

namespace ClipLine.P0Spike;

/// <summary>
/// 32bpp 预乘 alpha 的 DIB 画布，用 UpdateLayeredWindow 上屏。
/// 只用 BCL + GDI，不依赖 Win2D / DirectComposition —— 这是 P0 一次性验证切片的绘制路径。
/// </summary>
internal sealed unsafe class RopeCanvas : IDisposable
{
    internal const int CardWidth = 150;
    internal const int CardHeight = 104;

    private readonly int _width;
    private readonly int _height;
    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private uint* _bits;
    private bool _disposed;

    internal int Width => _width;
    internal int Height => _height;
    internal double Sag { get; }
    internal int CardLeft { get; }
    internal int CardTop { get; }

    internal RopeCanvas(int width, int height)
    {
        _width = width;
        _height = height;
        Sag = Math.Min(30, width * 0.018);

        var header = new Native.BITMAPINFOHEADER
        {
            BiSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            BiWidth = width,
            BiHeight = -height, // 负高度 = top-down，_bits[0] 即左上角
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = 0, // BI_RGB
        };

        _screenDc = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(_screenDc);
        _dib = Native.CreateDIBSection(_memDc, ref header, Native.DIB_RGB_COLORS,
            out IntPtr bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
            throw new InvalidOperationException("CreateDIBSection 失败，无法创建 32bpp 画布。");

        _bits = (uint*)bits.ToPointer();
        _oldBitmap = Native.SelectObject(_memDc, _dib);

        CardLeft = (width - CardWidth) / 2;
        CardTop = (int)RopeY(CardLeft + CardWidth / 2.0) + 12;
    }

    /// <summary>源码公式：ropeY(x) = 10 + 4·sag·(x/w)·(1−x/w)，sag = min(30, 宽×0.018)。</summary>
    internal double RopeY(double x) => 10 + 4 * Sag * (x / _width) * (1 - x / _width);

    internal void Render(bool hover)
    {
        new Span<uint>(_bits, _width * _height).Clear();
        DrawRope();
        DrawCard(hover);
    }

    private void DrawRope()
    {
        double fadeSpan = _width * 0.08;
        for (int x = 0; x < _width; x++)
        {
            double y = RopeY(x);
            double fade = Math.Clamp(x / fadeSpan, 0, 1) * Math.Clamp((_width - 1 - x) / fadeSpan, 0, 1);
            // 三层描边：阴影 / 主体 / 高光
            Stroke(x, y + 1.2, 1.4, 0, 0, 0, (int)(56 * fade));
            Stroke(x, y, 1.2, 255, 255, 255, (int)(140 * fade));
            Stroke(x, y - 0.35, 0.4, 255, 255, 255, (int)(115 * fade));
        }
    }

    private void Stroke(int x, double centerY, double thickness, int r, int g, int b, int alpha)
    {
        if (alpha <= 0) return;
        int top = (int)Math.Floor(centerY - thickness / 2);
        int bottom = (int)Math.Ceiling(centerY + thickness / 2);
        for (int y = top; y <= bottom; y++)
        {
            double coverage = Math.Clamp(thickness / 2 + 0.5 - Math.Abs(y - centerY), 0, 1);
            if (coverage > 0) Put(x, y, r, g, b, (int)(alpha * coverage));
        }
    }

    private void DrawCard(bool hover)
    {
        int x = CardLeft, y = CardTop, w = CardWidth, h = CardHeight;

        // 阴影
        FillRoundRect(x + 2, y + 5, w, h, 16, 0, 0, 0, (int)(255 * (hover ? 0.26 : 0.18)));
        // 外描边 0.5pt 黑 .10
        FillRoundRect(x, y, w, h, 16, 0, 0, 0, 26);
        // 玻璃相框
        FillRoundRect(x + 1, y + 1, w - 2, h - 2, 15, 255, 255, 255, 178);
        // 上白 .55 → 下白 .12 的渐变描边（用顶部亮线近似）
        for (int i = x + 16; i < x + w - 16; i++)
            Put(i, y, 255, 255, 255, 140);

        // 照片：按比例内嵌、圆角 12，浅蓝 → 白渐变 + 简化的窗口示意
        int px = x + 4, py = y + 4, pw = w - 8, ph = h - 8;
        for (int yy = py; yy < py + ph; yy++)
        {
            double t = (yy - py) / (double)ph;
            int r = (int)(120 + 110 * t), g = (int)(150 + 95 * t), b = (int)(225 + 30 * t);
            for (int xx = px; xx < px + pw; xx++)
            {
                int cx = Math.Clamp(xx, px + 12, px + pw - 1 - 12);
                int cy = Math.Clamp(yy, py + 12, py + ph - 1 - 12);
                int dx = xx - cx, dy = yy - cy;
                if (dx * dx + dy * dy <= 144) Put(xx, yy, r, g, b, 255);
            }
        }
        // 示意：深一点的标题条与两行内容块
        FillRect(px + 10, py + 10, pw - 20, 10, 79, 91, 213, 220);
        FillRect(px + 10, py + 28, (int)(pw * 0.55), 6, 255, 255, 255, 190);
        FillRect(px + 10, py + 40, (int)(pw * 0.38), 6, 255, 255, 255, 160);
        FillRect(px + 10, py + 52, (int)(pw * 0.70), 6, 255, 255, 255, 140);

        // 夹子 9×26（银色），顶部 8.5 处 5×1.4 卡槽
        int clipX = x + w / 2 - 4, clipY = y - 18;
        for (int yy = clipY; yy < clipY + 26; yy++)
        {
            double t = (yy - clipY) / 25.0;
            int c = (int)(210 - 70 * t);
            for (int xx = clipX; xx < clipX + 9; xx++)
            {
                int edge = (xx == clipX || xx == clipX + 8) ? -40 : 0;
                Put(xx, yy, Math.Clamp(c + edge, 0, 255), Math.Clamp(c + edge, 0, 255), Math.Clamp(c + edge + 6, 0, 255), 255);
            }
        }
        FillRect(clipX + 2, clipY + 8, 5, 2, 90, 95, 105, 255);

        // 悬停时左上角角标 20×20（P0 只验证视觉，不接命中）
        if (hover)
        {
            FillCircle(x - 6, y - 6, 10, 255, 255, 255, 210);
            FillCircle(x - 6, y - 6, 9, 79, 91, 213, 230);
        }
    }

    private void FillRoundRect(int x, int y, int w, int h, int radius, int r, int g, int b, int a)
    {
        for (int yy = y; yy < y + h; yy++)
        {
            int cy = Math.Clamp(yy, y + radius, y + h - 1 - radius);
            for (int xx = x; xx < x + w; xx++)
            {
                int cx = Math.Clamp(xx, x + radius, x + w - 1 - radius);
                int dx = xx - cx, dy = yy - cy;
                if (dx * dx + dy * dy <= radius * radius) Put(xx, yy, r, g, b, a);
            }
        }
    }

    private void FillRect(int x, int y, int w, int h, int r, int g, int b, int a)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++) Put(xx, yy, r, g, b, a);
    }

    private void FillCircle(int left, int top, int radius, int r, int g, int b, int a)
    {
        for (int yy = -radius; yy <= radius; yy++)
            for (int xx = -radius; xx <= radius; xx++)
                if (xx * xx + yy * yy <= radius * radius)
                    Put(left + radius + xx, top + radius + yy, r, g, b, a);
    }

    /// <summary>预乘 alpha 的 source-over 混合，写入 0xAABBGGRR 像素。</summary>
    private void Put(int x, int y, int r, int g, int b, int a)
    {
        if (a <= 0 || (uint)x >= (uint)_width || (uint)y >= (uint)_height) return;
        if (a > 255) a = 255;

        uint index = (uint)(y * _width + x);
        uint dst = _bits[index];
        uint sa = (uint)a;
        uint inverse = 255 - sa;

        uint dstA = (dst >> 24) & 0xFF;
        uint outA = sa + dstA * inverse / 255;

        uint sr = (uint)(r * a / 255), sg = (uint)(g * a / 255), sb = (uint)(b * a / 255);
        uint dr = dst & 0xFF, dg = (dst >> 8) & 0xFF, db = (dst >> 16) & 0xFF;

        uint outR = sr + dr * inverse / 255;
        uint outG = sg + dg * inverse / 255;
        uint outB = sb + db * inverse / 255;

        _bits[index] = (outA << 24)
            | (Math.Min(outB, 255u) << 16)
            | (Math.Min(outG, 255u) << 8)
            | Math.Min(outR, 255u);
    }

    /// <summary>
    /// 把预乘 alpha 画布合成到一块深蓝桌面底色上，导出 24bpp BMP 供人工核对几何。
    /// 仅用于 P0 自检，不参与产品渲染。
    /// </summary>
    internal void DumpPreviewBmp(string path)
    {
        int rowBytes = _width * 3;
        int padding = (4 - rowBytes % 4) % 4;
        int imageSize = (rowBytes + padding) * _height;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);              // "BM"
        writer.Write((uint)(14 + 40 + imageSize));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((uint)(14 + 40));
        writer.Write(40u);                         // BITMAPINFOHEADER
        writer.Write(_width);
        writer.Write(_height);                     // 正高度 = bottom-up
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0u);                          // BI_RGB
        writer.Write((uint)imageSize);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0u);
        writer.Write(0u);

        for (int y = _height - 1; y >= 0; y--)
        {
            for (int x = 0; x < _width; x++)
            {
                uint p = _bits[y * _width + x];
                uint a = (p >> 24) & 0xFF;
                uint b = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, r = p & 0xFF;
                uint inv = 255 - a;
                writer.Write((byte)(b + 180 * inv / 255)); // 桌面底色 B
                writer.Write((byte)(g + 100 * inv / 255)); // G
                writer.Write((byte)(r + 46 * inv / 255));  // R
            }
            for (int i = 0; i < padding; i++) writer.Write((byte)0);
        }
    }

    /// <summary>把画布合成到分层窗口。返回 UpdateLayeredWindow 是否成功。</summary>
    internal bool Present(IntPtr hWnd, int left, int top)
    {
        var dst = new Native.POINT(left, top);
        var size = new Native.SIZE(_width, _height);
        var src = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        return Native.UpdateLayeredWindow(hWnd, _screenDc, ref dst, ref size,
            _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_oldBitmap != IntPtr.Zero) Native.SelectObject(_memDc, _oldBitmap);
        if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) Native.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, _screenDc);
        _bits = null;
    }
}
