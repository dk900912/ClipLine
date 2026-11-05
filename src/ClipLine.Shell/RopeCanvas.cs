using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>
/// v1 的 32bpp 预乘 alpha DIB 画布（P0 切片验证过的 GDI 路径）。
/// 卡片内容用系统 GDI+ 解码真实截图并按等比 contain 绘制；解码失败回退到渐变占位。
/// 几何常量全部来自 <see cref="Layout"/>，不另起一套参数。
/// Win2D / DirectComposition 升级仍属后续迭代，见 docs/08。
/// </summary>
internal sealed unsafe class RopeCanvas : IDisposable
{
    private readonly int _widthPx;
    private readonly int _heightPx;
    private readonly double _scale;
    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private uint* _bits;
    private bool _disposed;

    private readonly List<(int X, int Y, int W, int H)> _cardRects = new();
    private readonly ThumbnailCache _thumbs = new();

    internal int WidthPx => _widthPx;
    internal int HeightPx => _heightPx;
    internal IReadOnlyList<(int X, int Y, int W, int H)> CardRects => _cardRects;

    internal RopeCanvas(int widthPx, int heightPx, double scale)
    {
        _widthPx = widthPx;
        _heightPx = heightPx;
        _scale = scale <= 0 ? 1.0 : scale;

        var header = new ShellNative.BITMAPINFOHEADER
        {
            BiSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.BITMAPINFOHEADER>(),
            BiWidth = widthPx,
            BiHeight = -heightPx, // 负高度 = top-down
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = 0,
        };

        _screenDc = ShellNative.GetDC(IntPtr.Zero);
        _memDc = ShellNative.CreateCompatibleDC(_screenDc);
        _dib = ShellNative.CreateDIBSection(_memDc, ref header, ShellNative.DibRgbColors,
            out IntPtr bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
            throw new InvalidOperationException("CreateDIBSection 失败，无法创建 32bpp 画布。");

        _bits = (uint*)bits.ToPointer();
        _oldBitmap = ShellNative.SelectObject(_memDc, _dib);
    }

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    /// <summary>按状态帧渲染：绳 + 可见卡片的真实缩略图。卡片居中排布，间距来自 Layout.Spacing。</summary>
    internal void Render(RopeFrame frame)
    {
        new Span<uint>(_bits, _widthPx * _heightPx).Clear();
        _cardRects.Clear();
        DrawRope();
        DrawCards(frame);
    }

    private void DrawRope()
    {
        double widthDip = _widthPx / _scale;
        double fadeSpan = _widthPx * 0.08;
        for (int x = 0; x < _widthPx; x++)
        {
            double y = Layout.RopeY(x / _scale, widthDip) * _scale;
            double fade = Math.Clamp(x / fadeSpan, 0, 1) * Math.Clamp((_widthPx - 1 - x) / fadeSpan, 0, 1);
            Stroke(x, y + 1.2, 1.4, 0, 0, 0, (int)(56 * fade));
            Stroke(x, y, 1.2, 255, 255, 255, (int)(140 * fade));
            Stroke(x, y - 0.35, 0.4, 255, 255, 255, (int)(115 * fade));
        }
    }

    private void DrawCards(RopeFrame frame)
    {
        int cardW = Px(Layout.CardWidth(frame.Size));
        int cardH = Px(Layout.PhotoMaxHeight(frame.Size));
        int spacing = Px(Layout.Spacing(frame.Size));
        int count = Math.Min(frame.VisibleItemCount, frame.VisibleCount);
        if (count <= 0) return;

        int totalW = spacing * (count - 1) + cardW;
        int left = (_widthPx - totalW) / 2;
        for (int i = 0; i < count; i++)
        {
            int x = left + i * spacing;
            int y = (int)(Layout.RopeY((x + cardW / 2.0) / _scale, _widthPx / _scale) * _scale) + Px(12);
            _cardRects.Add((x, y, cardW, cardH));
            CardVisual? card = i < frame.Cards.Count ? frame.Cards[i] : null;
            DrawCard(x, y, cardW, cardH, card?.Kept ?? i < frame.KeptCount, card?.Path);
        }
    }

    private void DrawCard(int x, int y, int w, int h, bool kept, string? path)
    {
        int radius = Px(16);
        FillRoundRect(x + 2, y + 5, w, h, radius, 0, 0, 0, 46);
        FillRoundRect(x, y, w, h, radius, 0, 0, 0, 26);
        FillRoundRect(x + 1, y + 1, w - 2, h - 2, radius - 1, 255, 255, 255, 178);

        int px = x + Px(4), py = y + Px(4), pw = w - Px(8), ph = h - Px(8);
        int innerRadius = Px(12);
        for (int yy = py; yy < py + ph; yy++)
        {
            double t = (yy - py) / (double)Math.Max(ph, 1);
            int r = (int)(120 + 110 * t), g = (int)(150 + 95 * t), b = (int)(225 + 30 * t);
            for (int xx = px; xx < px + pw; xx++)
            {
                int cx = Math.Clamp(xx, px + innerRadius, px + pw - 1 - innerRadius);
                int cy = Math.Clamp(yy, py + innerRadius, py + ph - 1 - innerRadius);
                int dx = xx - cx, dy = yy - cy;
                if (dx * dx + dy * dy <= innerRadius * innerRadius) Put(xx, yy, r, g, b, 255);
            }
        }

        // 真实截图：GDI+ 等比 contain 解码后的 BGRA 覆盖到照片内区，沿用同一套圆角裁剪。
        if (!string.IsNullOrEmpty(path))
        {
            byte[]? pixels = _thumbs.Get(path, pw, ph);
            if (pixels is not null && pixels.Length >= pw * ph * 4)
            {
                int maxX = Math.Max(innerRadius, pw - 1 - innerRadius);
                int maxY = Math.Max(innerRadius, ph - 1 - innerRadius);
                for (int yy = 0; yy < ph; yy++)
                {
                    int cy = Math.Clamp(yy, innerRadius, maxY);
                    int dy = yy - cy;
                    for (int xx = 0; xx < pw; xx++)
                    {
                        int src = (yy * pw + xx) * 4;
                        int alpha = pixels[src + 3];
                        if (alpha == 0) continue;
                        int cx = Math.Clamp(xx, innerRadius, maxX);
                        int dx = xx - cx;
                        if (dx * dx + dy * dy > innerRadius * innerRadius) continue;
                        // GDI+ LockBits(32bppARGB) 在本机返回 RGBA 字节序；
                        // 早期按 BGRA 解读会让橙/蓝互换（e2e 截图实测）。
                        Put(px + xx, py + yy, pixels[src], pixels[src + 1], pixels[src + 2], alpha);
                    }
                }
            }
        }

        // 夹子：固定照片用深空灰，其余银色（docs/05 §组件规格）
        int clipW = Px(9), clipH = Px(26);
        int clipX = x + w / 2 - clipW / 2, clipY = y - Px(18);
        for (int yy = clipY; yy < clipY + clipH; yy++)
        {
            double t = (yy - clipY) / (double)Math.Max(clipH - 1, 1);
            int c = kept ? (int)(90 - 30 * t) : (int)(210 - 70 * t);
            for (int xx = clipX; xx < clipX + clipW; xx++)
            {
                int edge = (xx == clipX || xx == clipX + clipW - 1) ? -40 : 0;
                Put(xx, yy,
                    Math.Clamp(c + edge, 0, 255),
                    Math.Clamp(c + edge, 0, 255),
                    Math.Clamp(c + edge + 6, 0, 255), 255);
            }
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

    private void Put(int x, int y, int r, int g, int b, int a)
    {
        if (a <= 0 || (uint)x >= (uint)_widthPx || (uint)y >= (uint)_heightPx) return;
        if (a > 255) a = 255;

        uint index = (uint)(y * _widthPx + x);
        uint dst = _bits[index];
        uint sa = (uint)a;
        uint inverse = 255 - sa;

        uint dstA = (dst >> 24) & 0xFF;
        uint outA = sa + dstA * inverse / 255;
        uint sr = (uint)(r * a / 255), sg = (uint)(g * a / 255), sb = (uint)(b * a / 255);
        uint dr = dst & 0xFF, dg = (dst >> 8) & 0xFF, db = (dst >> 16) & 0xFF;

        _bits[index] = (outA << 24)
            | (Math.Min(sb + db * inverse / 255, 255u) << 16)
            | (Math.Min(sg + dg * inverse / 255, 255u) << 8)
            | Math.Min(sr + dr * inverse / 255, 255u);
    }

    /// <summary>把当前帧导出成 PNG，供冒烟/验证作为确定性渲染证据。</summary>
    internal bool SavePng(string path) =>
        GdiPlus.SavePremultipliedFrame((IntPtr)_bits, _widthPx, _heightPx, path);

    internal bool Present(IntPtr hWnd)
    {
        var dst = new ShellNative.POINT(0, 0);
        var size = new ShellNative.SIZE(_widthPx, _heightPx);
        var src = new ShellNative.POINT(0, 0);
        var blend = new ShellNative.BLENDFUNCTION
        {
            BlendOp = ShellNative.AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = ShellNative.AcSrcAlpha,
        };
        return ShellNative.UpdateLayeredWindow(hWnd, _screenDc, ref dst, ref size,
            _memDc, ref src, 0, ref blend, ShellNative.ULW_ALPHA);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_oldBitmap != IntPtr.Zero) ShellNative.SelectObject(_memDc, _oldBitmap);
        if (_dib != IntPtr.Zero) ShellNative.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) ShellNative.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) ShellNative.ReleaseDC(IntPtr.Zero, _screenDc);
        _bits = null;
    }
}

/// <summary>一张可见卡片的最小渲染信息。</summary>
public sealed record CardVisual(Guid Id, string Path, bool Kept);

/// <summary>
/// 画布需要的状态帧，避免壳层反向依赖门面快照的全部字段。
/// Signature 是内容指纹：渲染壳按帧比对，只有内容变化才重建 DIB（不会因引用变化每帧重绘）。
/// </summary>
public sealed record RopeFrame(
    CardSize Size,
    int VisibleCount,
    int VisibleItemCount,
    int KeptCount,
    IReadOnlyList<CardVisual> Cards,
    string Signature);
