using ClipLine.Domain;

namespace ClipLine.Shell;

/// <summary>
/// v1 的 32bpp 预乘 alpha DIB 画布（P0 切片验证过的 GDI 路径）。
/// 卡片内容用系统 GDI+ 解码真实截图并按等比 contain 绘制；解码失败回退到渐变占位。
/// 几何常量全部来自 <see cref="Layout"/>，不另起一套参数。
///
/// 抗锯齿（v1.1）：卡片先画进离屏 uint 缓冲，再按摆角做「反向映射 + 双线性重采样」
/// 合成到主画布 —— 旧的逐行整数位移会把垂直边框变成阶梯，静止/运动时圆角也是 1-bit
/// 硬边。现在圆角与裁剪用解析式 SDF 覆盖率，边缘有 1px 羽化；摆角为 0 时走 1:1 拷贝。
/// Win2D / DirectComposition 升级仍属后续迭代，见 docs/08、docs/10。
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

    private const int TitleHeightDip = 44;

    // 卡片离屏合成：_cardBuf 与主画布同为「预乘 BGRA + A<<24」，可直接线性插值。
    // 缓冲矩形用全局坐标 _cardOriginX/Y 定位，DrawCard 里的 Put 自动转成局部索引。
    private uint[] _cardBuf = Array.Empty<uint>();
    private int _cardW, _cardH;
    private int _cardOriginX, _cardOriginY;
    private bool _inCard;
    private double _cardAngle;                 // 弧度，绕夹子顶边中点
    private double _cardPivotX, _cardPivotY;  // 全局坐标
    private double _alphaScale = 1;           // 掉落层的整体淡出

    // 「已复制」角标的文字蒙版：GDI TextOutW 生成一次，之后逐帧只需按覆盖率贴图。
    private byte[]? _badgeMask;
    private int _badgeW, _badgeH;

    internal int WidthPx => _widthPx;
    internal int HeightPx => _heightPx;
    internal IReadOnlyList<(int X, int Y, int W, int H)> CardRects => _cardRects;

    /// <summary>键盘焦点卡片序号；-1 表示无焦点。绘制 2 DIP 品牌色焦点框。</summary>
    internal int FocusIndex { get; set; } = -1;

    /// <summary>指针悬停卡片序号；-1 表示没有悬停。绘制 1.5 DIP 弱描边。</summary>
    internal int HoverIndex { get; set; } = -1;

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
        _inCard = false;
        _alphaScale = 1;
        DrawRope();
        DrawCards(frame);
        _inCard = false;
    }

    /// <summary>
    /// 掉落层：只画正在离场的卡片，不画绳子。对齐 CaptureFlight.updateFall：
    /// e = raw³，下落 520 DIP、倾角 tilt + (tilt×7+20)·e、透明度 1 − e。
    /// 旋转支点同样是夹子顶边中点，走与主画布相同的双线性合成路径。
    /// </summary>
    internal void RenderFall(IReadOnlyList<FallCard> falls)
    {
        new Span<uint>(_bits, _widthPx * _heightPx).Clear();
        _cardRects.Clear();
        _inCard = false;
        _alphaScale = 1;
        foreach (FallCard f in falls)
        {
            double e = Math.Pow(Math.Clamp(f.Progress, 0, 1), 3);
            int dy = Px(CaptureFlight.FallDistanceDip * e);
            double angle = f.Tilt + (f.Tilt * 7 + 20) * e;
            int y = f.Y + dy;
            _alphaScale = 1 - e;
            BeginCard(f.X, y, f.W, f.H, angle);
            DrawCard(f.X, y, f.W, f.H, f.Kept, f.Path, false);
            EndCard();
            _inCard = false;
            _alphaScale = 1;
        }
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
            // 命中矩形用未摆动、未落下的静止位置，避免指针跟随卡片抖动。
            _cardRects.Add((x, y, cardW, cardH));
            CardVisual? card = i < frame.Cards.Count ? frame.Cards[i] : null;
            double swing = card?.SwingDegrees ?? 0;
            int drawY = y + Px(card?.DropDip ?? 0);
            BeginCard(x, drawY, cardW, cardH, swing);
            DrawCard(x, drawY, cardW, cardH, card?.Kept ?? i < frame.KeptCount, card?.Path,
                card?.Copied ?? false, focused: i == FocusIndex, hovered: i == HoverIndex);
            EndCard();
        }
        _inCard = false;
        if (frame.KeptCount > 0) DrawKnot(left, spacing, cardW, count, frame.KeptCount);
    }

    /// <summary>
    /// V-09 绳结：仅当存在固定照片时出现，7×5.5 DIP 灰色椭圆 + 两条 1pt 松散线头，
    /// 位置在最后一张固定照片与下一张之间（全固定时取半个间距外）。
    /// 同时把绳结左侧的绳段染成淡靛蓝，明确标出「固定区 / 普通区」的分界。
    /// 椭圆用解析式覆盖率绘制，边缘带 1px 羽化、无阶梯。
    /// </summary>
    private void DrawKnot(int left, int spacing, int cardW, int count, int keptCount)
    {
        int knotX = keptCount < count
            ? (left + (keptCount - 1) * spacing + cardW + left + keptCount * spacing) / 2
            : left + (count - 1) * spacing + cardW + spacing / 2;
        double widthDip = _widthPx / _scale;
        int knotY = (int)Math.Round(Layout.RopeY(knotX / _scale, widthDip) * _scale);
        int rx = Math.Max(2, Px(3.5));
        int ry = Math.Max(2, Px(2.75));

        // 染色必须和 DrawRope 一样走亚像素 Stroke：若用 (int)Math.Round(y) 写整数行，
        // y 的小数部分跨整时染色带会突然多/少一行，6× 放大可见一处厚度台阶。
        for (int x = Px(24); x < knotX - rx; x++)
        {
            double y = Layout.RopeY(x / _scale, widthDip) * _scale;
            StrokeRgb(x, y, 1.2, 0x6C63F4, 168);
            StrokeRgb(x, y + 1.2, 1.4, 0x6C63F4, 58);
            StrokeRgb(x, y - 0.35, 0.4, 0x8E88FA, 60);
        }

        for (int yy = knotY - ry - 2; yy <= knotY + ry + 2; yy++)
            for (int xx = knotX - rx - 2; xx <= knotX + rx + 2; xx++)
            {
                double nx = (xx + 0.5 - knotX) / (double)rx;
                double ny = (yy + 0.5 - knotY) / (double)ry;
                double d = Math.Sqrt(nx * nx + ny * ny);
                double cov = Math.Clamp((1 - d) * Math.Min(rx, ry), 0, 1);
                if (cov <= 0) continue;
                // 左上高光、右下暗部，让 7×5.5 DIP 的小结也有体积感。
                int shade = Math.Clamp(0x6E - (int)((nx + ny) * 14), 0x50, 0x96);
                PutRgb(xx, yy, (shade << 16) | (shade << 8) | shade, 255, cov);
            }

        // 两条松散线头：向外下方自然垂落。
        DrawKnotThread(knotX - 1, knotY + ry - 1, knotX - rx * 2, knotY + ry + Px(4));
        DrawKnotThread(knotX + 1, knotY + ry - 1, knotX + rx * 2, knotY + ry + Px(3));
    }

    private void DrawKnotThread(int x0, int y0, int x1, int y1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        if (steps <= 0) return;
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            int x = (int)Math.Round(x0 + (x1 - x0) * t);
            int y = (int)Math.Round(y0 + (y1 - y0) * t + 2 * Math.Sin(Math.PI * t));
            PutRgb(x, y, 0x7C8394, 235, 0.95);
            PutRgb(x, y + 1, 0x7C8394, 130, 0.8);
        }
    }

    /// <summary>
    /// 开一张离屏卡片。缓冲范围要含阴影（x+2,y+5）与夹子（y−18），
    /// 再加 2px 余量；旋转后的包围盒由 EndCard 在目标画布上另算，不受此矩形限制。
    /// </summary>
    private void BeginCard(int x, int y, int w, int h, double swingDegrees)
    {
        const int pad = 2;
        int clipH = Px(18);
        _cardOriginX = x - pad;
        _cardOriginY = y - clipH - pad;
        _cardW = w + 2 + pad * 2;
        _cardH = h + 5 + clipH + pad * 2;
        int needed = _cardW * _cardH;
        if (_cardBuf.Length < needed) _cardBuf = new uint[needed];
        Array.Clear(_cardBuf, 0, needed);

        _cardAngle = swingDegrees * Math.PI / 180.0;
        _cardPivotX = x + w / 2.0;
        _cardPivotY = y - clipH;
        _inCard = true;
    }

    /// <summary>
    /// 把离屏卡片按绕支点的真旋转合成到主画布。对每个目标像素做反向映射，
    /// 源坐标带小数 → 双线性采样，边缘得到 0..255 的连续覆盖率，阶梯锯齿消失。
    /// 摆角为 0 时退化为 1:1 拷贝，静止帧零插值开销。
    /// </summary>
    private void EndCard()
    {
        _inCard = false;
        double a = _cardAngle;
        if (Math.Abs(a) < 0.0002)
        {
            for (int ly = 0; ly < _cardH; ly++)
            {
                int row = ly * _cardW;
                int ty = _cardOriginY + ly;
                for (int lx = 0; lx < _cardW; lx++)
                {
                    uint c = _cardBuf[row + lx];
                    if (c != 0) BlendRaw(_cardOriginX + lx, ty, c);
                }
            }
            return;
        }

        double cos = Math.Cos(a), sin = Math.Sin(a);
        // 正向旋转缓冲四角，得到目标画布上的包围盒。
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < 4; i++)
        {
            double cx = (i & 1) == 0 ? _cardOriginX : _cardOriginX + _cardW;
            double cy = (i & 2) == 0 ? _cardOriginY : _cardOriginY + _cardH;
            double dx = cx - _cardPivotX, dy = cy - _cardPivotY;
            double gx = _cardPivotX + cos * dx - sin * dy;
            double gy = _cardPivotY + sin * dx + cos * dy;
            if (gx < minX) minX = gx;
            if (gx > maxX) maxX = gx;
            if (gy < minY) minY = gy;
            if (gy > maxY) maxY = gy;
        }

        int x0 = Math.Max(0, (int)Math.Floor(minX) - 1);
        int y0 = Math.Max(0, (int)Math.Floor(minY) - 1);
        int x1 = Math.Min(_widthPx, (int)Math.Ceiling(maxX) + 1);
        int y1 = Math.Min(_heightPx, (int)Math.Ceiling(maxY) + 1);

        for (int ty = y0; ty < y1; ty++)
        {
            double dy = ty + 0.5 - _cardPivotY;
            for (int tx = x0; tx < x1; tx++)
            {
                double dx = tx + 0.5 - _cardPivotX;
                // R(-a)：目标像素中心反推源坐标中心。
                double rx = cos * dx + sin * dy;
                double ry = -sin * dx + cos * dy;
                double sx = _cardPivotX + rx - _cardOriginX - 0.5;
                double sy = _cardPivotY + ry - _cardOriginY - 0.5;
                uint color = SampleBilinear(sx, sy);
                if (color != 0) BlendRaw(tx, ty, color);
            }
        }
    }

    private uint SampleBilinear(double sx, double sy)
    {
        int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
        uint p00 = CardAt(ix, iy), p10 = CardAt(ix + 1, iy);
        uint p01 = CardAt(ix, iy + 1), p11 = CardAt(ix + 1, iy + 1);
        uint any = p00 | p10 | p01 | p11;
        if (any == 0) return 0;

        double fx = sx - ix, fy = sy - iy;
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy);
        double w01 = (1 - fx) * fy, w11 = fx * fy;

        uint a = Mix((p00 >> 24) & 0xFF, (p10 >> 24) & 0xFF,
            (p01 >> 24) & 0xFF, (p11 >> 24) & 0xFF, w00, w10, w01, w11);
        if (a == 0) return 0;
        uint b = Mix((p00 >> 16) & 0xFF, (p10 >> 16) & 0xFF,
            (p01 >> 16) & 0xFF, (p11 >> 16) & 0xFF, w00, w10, w01, w11);
        uint g = Mix((p00 >> 8) & 0xFF, (p10 >> 8) & 0xFF,
            (p01 >> 8) & 0xFF, (p11 >> 8) & 0xFF, w00, w10, w01, w11);
        uint r = Mix(p00 & 0xFF, p10 & 0xFF, p01 & 0xFF, p11 & 0xFF,
            w00, w10, w01, w11);
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    private static uint Mix(uint c00, uint c10, uint c01, uint c11,
        double w00, double w10, double w01, double w11) =>
        (uint)Math.Round(c00 * w00 + c10 * w10 + c01 * w01 + c11 * w11);

    private uint CardAt(int lx, int ly) =>
        (uint)lx < (uint)_cardW && (uint)ly < (uint)_cardH
            ? _cardBuf[ly * _cardW + lx] : 0;

    private void DrawCard(int x, int y, int w, int h, bool kept, string? path, bool copied,
        bool focused = false, bool hovered = false)
    {
        int radius = Px(16);
        FillRoundRect(x + 2, y + 5, w, h, radius, 0, 0, 0, 46);
        // V-09 / D-02：固定照片除深空灰夹子外，还给一圈品牌靛蓝细描边 + 淡靛蓝底。
        // mac 原版只有夹子与绳结，在小尺寸高 DPI 单屏下几乎看不出差别，这是 Win 版
        // 有意增强的可辨识性（见 docs/05 §组件规格 偏差记录）。
        // 必须走 FillRoundRectRgb(#RRGGBB)：直接给 FillRoundRect 传 r,g,b 会按
        // DIB 的 B/G/R 内存序反解，靛蓝会变成粉红（真实渲染帧实测过）。
        if (kept) FillRoundRectRgb(x, y, w, h, radius, 0x6C63F4, 210);
        else FillRoundRectRgb(x, y, w, h, radius, 0x000000, 26);
        FillRoundRectRgb(x + 1, y + 1, w - 2, h - 2, radius - 1,
            kept ? 0xE8EBFC : 0xFFFFFF, 178);

        int px = x + Px(4), py = y + Px(4), pw = w - Px(8), ph = h - Px(8);
        int innerRadius = Px(12);
        // P2-11：非 3:2 图片的 contain 留白改成中性相框色（浅色 #F3F4F8 / 深色 #2B2B2B），
        // 原来的饱和蓝渐变会让竖图、方图四周出现一圈明显蓝边。
        int matte = Theme.Surface;
        for (int yy = py; yy < py + ph; yy++)
        {
            for (int xx = px; xx < px + pw; xx++)
            {
                double cov = RoundCoverage(xx + 0.5, yy + 0.5, px, py, pw, ph, innerRadius);
                if (cov > 0) PutRgb(xx, yy, matte, 255, cov);
            }
        }

        // 真实截图：GDI+ 等比 contain 解码后的 BGRA 覆盖到照片内区，沿用同一套圆角裁剪。
        if (!string.IsNullOrEmpty(path))
        {
            byte[]? pixels = _thumbs.Get(path, pw, ph);
            if (pixels is not null && pixels.Length >= pw * ph * 4)
            {
                for (int yy = 0; yy < ph; yy++)
                {
                    double covY = yy + 0.5;
                    for (int xx = 0; xx < pw; xx++)
                    {
                        int src = (yy * pw + xx) * 4;
                        int alpha = pixels[src + 3];
                        if (alpha == 0) continue;
                        double cov = RoundCoverage(px + xx + 0.5, py + covY,
                            px, py, pw, ph, innerRadius);
                        if (cov <= 0) continue;
                        // GDI+ LockBits(32bppARGB) 在本机返回 RGBA 字节序；
                        // 早期按 BGRA 解读会让橙/蓝互换（e2e 截图实测）。
                        PutCoverage(px + xx, py + yy,
                            pixels[src], pixels[src + 1], pixels[src + 2], alpha, cov);
                    }
                }
            }
        }

        // 夹子：固定照片用深空灰，其余银色（docs/05 §组件规格）
        // P2-9：改成圆角 3.5 DIP 的金属渐变 + 顶部卡槽 + 下沿投影 + 左侧高光，
        // 旧的直角矩形放大后就是一块纯色方块。
        int clipW = Px(9), clipH = Px(26);
        int clipX = x + w / 2 - clipW / 2, clipY = y - Px(18);
        int clipR = Math.Max(2, Px(3.5));
        int shadowRows = Math.Max(1, Px(3));
        for (int i = 0; i < shadowRows; i++)
        {
            int alpha = (int)(44 * (1 - i / (double)shadowRows));
            for (int xx = clipX - Px(1); xx < clipX + clipW + Px(1); xx++)
                PutRgb(xx, clipY + clipH - Px(1) + i, 0x0B0F16, alpha);
        }
        for (int yy = clipY; yy < clipY + clipH; yy++)
        {
            double t = (yy - clipY) / (double)Math.Max(clipH - 1, 1);
            int grey = kept ? (int)(98 - 36 * t) : (int)(224 - 70 * t);
            int rgb = (grey << 16) | (grey << 8) | Math.Clamp(grey + 8, 0, 255);
            for (int xx = clipX; xx < clipX + clipW; xx++)
            {
                double cov = RoundCoverage(xx + 0.5, yy + 0.5, clipX, clipY, clipW, clipH, clipR);
                if (cov > 0) PutRgb(xx, yy, rgb, 255, cov);
            }
        }
        // 左侧金属高光
        int glossX = clipX + Math.Max(1, Px(1.6));
        for (int yy = clipY + Px(4); yy < clipY + clipH - Px(5); yy++)
            PutRgb(glossX, yy, kept ? 0x9AA0AA : 0xF4F6F9, 150);
        // 顶部卡槽（金属夹的咬合缝）
        int slotW = Px(5), slotH = Math.Max(1, Px(1.4));
        int slotX = clipX + (clipW - slotW) / 2, slotY = clipY + Px(4);
        for (int yy = slotY; yy < slotY + slotH; yy++)
            for (int xx = slotX; xx < slotX + slotW; xx++)
                PutRgb(xx, yy, kept ? 0x2A2E35 : 0x8C929B, 190);

        // G-01 / V-17：单击复制后 1.2 秒内在卡片上显示「已复制」。
        if (copied) DrawCopiedBadge(x, y, w, h);

        // P0-6：键盘焦点框 —— 2 DIP 品牌色内描边 + 外侧 40% 透明度扩边，
        // 保证在任意背景（深色 / 高对比度 / 照片本身是靛蓝）上都看得见。
        // P1 补充：悬停给一圈 1.5 DIP 的品牌色描边（比焦点框弱），
        // 让「这张卡可以点」在鼠标移上去的瞬间就能看出来。
        if (hovered && !focused)
            DrawCardRing(x, y, w, h, radius, 0, Math.Max(1, Px(1.5)), 0,
                Theme.AccentHover, 120, 0);
        // P0-6：键盘焦点框 —— 2 DIP 品牌色内描边 + 外侧柔光。
        if (focused)
            DrawCardRing(x, y, w, h, radius, Px(1), Math.Max(2, Px(2)), Px(3),
                Theme.Accent, 255, 90);
    }

    /// <summary>
    /// 卡片描边环：外环 - 内环的覆盖率差 = 环带。halo &gt; 0 时外扩部分用低透明度
    /// 做柔光，保证在照片本身也是靛蓝时依然可辨。
    /// </summary>
    private void DrawCardRing(int x, int y, int w, int h, int radius, int inset, int ring,
        int halo, int rgb, int coreAlpha, int haloAlpha)
    {
        int outerR = Math.Max(2, radius - inset);
        for (int yy = y - halo; yy < y + h + halo; yy++)
            for (int xx = x - halo; xx < x + w + halo; xx++)
            {
                double outer = RoundCoverage(xx + 0.5, yy + 0.5,
                    x + inset - halo, y + inset - halo,
                    w - (inset - halo) * 2, h - (inset - halo) * 2, outerR + halo);
                double inner = RoundCoverage(xx + 0.5, yy + 0.5,
                    x + inset + ring, y + inset + ring,
                    w - (inset + ring) * 2, h - (inset + ring) * 2,
                    Math.Max(2, outerR - ring));
                double cov = Math.Max(0, outer - inner);
                if (cov <= 0) continue;
                bool onRing = xx >= x + inset && xx < x + w - inset
                    && yy >= y + inset && yy < y + h - inset;
                PutRgb(xx, yy, rgb, onRing ? coreAlpha : haloAlpha, cov);
            }
    }

    /// <summary>品牌靛蓝圆角底 + 白色中文「已复制」，直接合成到卡片离屏缓冲。</summary>
    private void DrawCopiedBadge(int x, int y, int w, int h)
    {
        EnsureBadgeMask();
        int bw = _badgeW, bh = _badgeH;
        if (bw <= 0 || bh <= 0 || bw >= w || bh >= h) return;
        int bx = x + (w - bw) / 2;
        int by = y + h - bh - Px(8);
        int radius = Math.Max(1, Math.Min(bh / 2 - 1, Px(10)));
        // Put 的三色参数按 32bpp DIB 的 B/G/R 字节序，#4F5BD5 要传 (213, 91, 79)。
        FillRoundRect(bx, by, bw, bh, radius, 213, 91, 79, 246);
        FillRoundRect(bx + 1, by + 1, bw - 2, bh - 2, Math.Max(1, radius - 1), 213, 91, 79, 252);
        if (_badgeMask is null) return;
        for (int yy = 0; yy < bh; yy++)
            for (int xx = 0; xx < bw; xx++)
            {
                int alpha = _badgeMask[yy * bw + xx];
                if (alpha > 0) Put(bx + xx, by + yy, 255, 255, 255, alpha);
            }
    }

    /// <summary>
    /// GDI 32bpp DIB 上用 TextOutW 渲染中文，读回 RGB 作为抗锯齿覆盖率蒙版。
    /// GDI 写 32bpp DIB 时 alpha 字节恒为 0，所以不能直接在主画布上写字，
    /// 必须单独出蒙版再按 alpha 合成。生成一次后整帧复用，避免 30 fps 反复建字体。
    /// </summary>
    private void EnsureBadgeMask()
    {
        if (_badgeW > 0) return;
        int fontH = Math.Max(12, Px(13));
        int bw = fontH * 3 + Px(16);
        int bh = fontH + Px(10);
        _badgeW = bw;
        _badgeH = bh; // 即使文字生成失败也保留尺寸，纯色角标底仍可见

        IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, oldBmp = IntPtr.Zero;
        IntPtr font = IntPtr.Zero, oldFont = IntPtr.Zero;
        try
        {
            var header = new ShellNative.BITMAPINFOHEADER
            {
                BiSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.BITMAPINFOHEADER>(),
                BiWidth = bw,
                BiHeight = -bh,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0,
            };
            dc = ShellNative.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return;
            dib = ShellNative.CreateDIBSection(dc, ref header, ShellNative.DibRgbColors,
                out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return;
            oldBmp = ShellNative.SelectObject(dc, dib);
            new Span<uint>((void*)bits, bw * bh).Clear();

            // DEFAULT_CHARSET / ANTIALIASED_QUALITY / FW_BOLD / 中文字面
            font = ShellNative.CreateFontW(-fontH, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0,
                FaceFor("已复制"));
            if (font == IntPtr.Zero) return;
            oldFont = ShellNative.SelectObject(dc, font);
            ShellNative.SetBkMode(dc, 1); // TRANSPARENT
            ShellNative.SetTextColor(dc, 0x00FFFFFFu);
            ShellNative.TextOutW(dc, Px(8), (bh - fontH) / 2, "已复制", 3);

            byte[] mask = new byte[bw * bh];
            uint* pixels = (uint*)bits;
            for (int i = 0; i < mask.Length; i++)
            {
                uint value = pixels[i];
                int r = (int)(value & 0xFF);
                int g = (int)((value >> 8) & 0xFF);
                int b = (int)((value >> 16) & 0xFF);
                int a = Math.Max(r, Math.Max(g, b));
                if (a > 0) mask[i] = (byte)a;
            }
            _badgeMask = mask;
        }
        catch
        {
            // 字体或 GDI 失败时退回纯色角标 + 托盘气泡，绝不影响绳本体渲染。
        }
        finally
        {
            if (oldFont != IntPtr.Zero) ShellNative.SelectObject(dc, oldFont);
            if (font != IntPtr.Zero) ShellNative.DeleteObject(font);
            if (oldBmp != IntPtr.Zero) ShellNative.SelectObject(dc, oldBmp);
            if (dib != IntPtr.Zero) ShellNative.DeleteObject(dib);
            if (dc != IntPtr.Zero) ShellNative.DeleteDC(dc);
        }
    }

    /// <summary>
    /// 与 Stroke 同构的 #RRGGBB 版本。注意 Stroke/PutCoverage 的形参虽名为 r,g,b，
    /// 实际按 DIB 的 B/G/R 内存序写，因此必须和 PutRgb 一样低位在前拆通道；
    /// 若按逻辑 RGB 传参，靛蓝会变成粉红（真实证据帧实测过两次）。
    /// </summary>
    private void StrokeRgb(int x, double centerY, double thickness, int rgb, int alpha) =>
        Stroke(x, centerY, thickness,
            rgb & 0xFF, (rgb >> 8) & 0xFF, (rgb >> 16) & 0xFF, alpha);

    private void Stroke(int x, double centerY, double thickness, int r, int g, int b, int alpha)
    {
        if (alpha <= 0) return;
        int top = (int)Math.Floor(centerY - thickness / 2);
        int bottom = (int)Math.Ceiling(centerY + thickness / 2);
        for (int y = top; y <= bottom; y++)
        {
            double coverage = Math.Clamp(thickness / 2 + 0.5 - Math.Abs(y - centerY), 0, 1);
            if (coverage > 0) PutCoverage(x, y, r, g, b, alpha, coverage);
        }
    }

    /// <summary>
    /// 圆角矩形的解析式有符号距离：sd &lt; 0 在内部，0.5 − sd 即 1px 羽化的覆盖率。
    /// 取代原来的 dx²+dy² ≤ r² 二值判定，圆角与直边都得到连续边缘。
    /// </summary>
    private static double RoundCoverage(double pxX, double pxY, int x, int y, int w, int h, int radius)
    {
        double cx = x + w / 2.0, cy = y + h / 2.0;
        double qx = Math.Abs(pxX - cx) - (w / 2.0 - radius);
        double qy = Math.Abs(pxY - cy) - (h / 2.0 - radius);
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        double outside = Math.Sqrt(ox * ox + oy * oy);
        double inside = Math.Min(Math.Max(qx, qy), 0);
        return Math.Clamp(0.5 - (outside + inside - radius), 0, 1);
    }

    private void FillRoundRect(int x, int y, int w, int h, int radius, int r, int g, int b, int a)
    {
        if (w <= 0 || h <= 0 || a <= 0) return;
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                double cov = RoundCoverage(xx + 0.5, yy + 0.5, x, y, w, h, radius);
                if (cov > 0) PutCoverage(xx, yy, r, g, b, a, cov);
            }
    }

    private void Put(int x, int y, int r, int g, int b, int a) =>
        PutCoverage(x, y, r, g, b, a, 1);

    /// <summary>把带覆盖率的源像素写成预乘 BGRA，再 src-over 到当前目标（主画布或卡片缓冲）。</summary>
    private void PutCoverage(int x, int y, int r, int g, int b, int a, double coverage)
    {
        double alpha = a * coverage * _alphaScale;
        if (alpha <= 0) return;
        if (alpha > 255) alpha = 255;
        uint sa = (uint)(int)Math.Round(alpha);
        if (sa == 0) return;

        uint color = (sa << 24)
            | ((uint)(b * sa / 255) << 16)
            | ((uint)(g * sa / 255) << 8)
            | (uint)(r * sa / 255);

        if (_inCard)
        {
            int lx = x - _cardOriginX, ly = y - _cardOriginY;
            if ((uint)lx < (uint)_cardW && (uint)ly < (uint)_cardH)
            {
                int idx = ly * _cardW + lx;
                _cardBuf[idx] = Over(_cardBuf[idx], color);
            }
            return;
        }
        BlendRaw(x, y, color);
    }

    private void BlendRaw(int x, int y, uint color)
    {
        if ((uint)x >= (uint)_widthPx || (uint)y >= (uint)_heightPx) return;
        uint index = (uint)(y * _widthPx + x);
        _bits[index] = Over(_bits[index], color);
    }

    /// <summary>预乘 alpha 的 src-over；预乘格式也是双线性插值可以直接按通道加权的前提。</summary>
    private static uint Over(uint dst, uint src)
    {
        uint sa = (src >> 24) & 0xFF;
        if (sa == 0) return dst;
        if (sa == 255) return src;
        uint inverse = 255 - sa;
        uint dstA = (dst >> 24) & 0xFF;
        uint outA = sa + dstA * inverse / 255;
        uint sr = src & 0xFF, sg = (src >> 8) & 0xFF, sb = (src >> 16) & 0xFF;
        uint dr = dst & 0xFF, dg = (dst >> 8) & 0xFF, db = (dst >> 16) & 0xFF;
        return (outA << 24)
            | (Math.Min(sb + db * inverse / 255, 255u) << 16)
            | (Math.Min(sg + dg * inverse / 255, 255u) << 8)
            | Math.Min(sr + dr * inverse / 255, 255u);
    }

    /// <summary>
    /// 抗锯齿回归证据：统计矩形内 0 &lt; alpha &lt; 255 的像素数与不同 alpha 级数。
    /// 二值硬边只会产生 0/255；出现多级中间值说明边缘是连续过渡。
    /// </summary>
    /// <summary>
    /// 读取帧内单个像素的反预乘逻辑 RGBA。_bits 是 BGRA 预乘 DIB，
    /// 这里显式换成 R/G/B 顺序，避免冒烟断言沿用渲染层那套 B/G/R 形参而误判色序。
    /// </summary>
    internal (int R, int G, int B, int A) PixelAt(int x, int y)
    {
        if (_bits == null || (uint)x >= (uint)_widthPx || (uint)y >= (uint)_heightPx)
            return (0, 0, 0, 0);
        uint c = _bits[y * _widthPx + x];
        int a = (int)((c >> 24) & 0xFF);
        if (a == 0) return (0, 0, 0, 0);
        return (Math.Min(255, (int)((c >> 16) & 0xFF) * 255 / a),
                Math.Min(255, (int)((c >> 8) & 0xFF) * 255 / a),
                Math.Min(255, (int)(c & 0xFF) * 255 / a), a);
    }

    internal (int Count, int Levels) AlphaStats(int x0, int y0, int x1, int y1)
    {
        int left = Math.Max(0, x0), top = Math.Max(0, y0);
        int right = Math.Min(_widthPx, x1), bottom = Math.Min(_heightPx, y1);
        var levels = new bool[256];
        int count = 0, distinct = 0;
        for (int y = top; y < bottom; y++)
        {
            uint index = (uint)(y * _widthPx + left);
            for (int x = left; x < right; x++, index++)
            {
                int a = (int)((_bits[index] >> 24) & 0xFF);
                if (a <= 0 || a >= 255) continue;
                count++;
                if (!levels[a]) { levels[a] = true; distinct++; }
            }
        }
        return (count, distinct);
    }

    // ------------------------------------------------------------------
    // 系统替身窗口（v1.4）：快速查看 / 标注 / 修剪 / 拿在手上
    // 四个模式共用 Fluent chrome，内容区彼此完全不同，取代原先统一降级到
    // ShellExecuteW("open") 的占位行为。见 docs/05 §7、docs/11。
    // ------------------------------------------------------------------

    internal ThemePalette Theme { get; set; } = ThemePalette.Light;

    internal void RenderTool(ToolFrame frame, ToolLayout layout)
    {
        new Span<uint>(_bits, _widthPx * _heightPx).Clear();
        _cardRects.Clear();
        _inCard = false;
        _alphaScale = 1;
        layout.Reset();

        ThemePalette t = Theme;
        int pad = Px(8);
        int bx = pad, by = pad;
        int bw = _widthPx - pad * 2;
        int bh = _heightPx - pad * 2 - Px(2);
        int radius = Px(10);
        if (bw <= Px(240) || bh <= Px(140)) return;

        // 6 层递减 alpha 的柔和投影，替代 3 层硬边圆角矩形（P2-1）。
        DrawSoftShadow(bx, by, bw, bh, radius, t.IsHighContrast ? 0 : 64);
        FillRoundRectRgb(bx, by, bw, bh, radius, t.Border, 255);
        FillRoundRectRgb(bx + 1, by + 1, bw - 2, bh - 2, Math.Max(1, radius - 1), t.Window, 255);

        int titleH = Px(TitleHeightDip);
        for (int yy = by + 1; yy < by + titleH; yy++)
            for (int xx = bx + 1; xx < bx + bw - 1; xx++)
            {
                double cov = RoundCoverage(xx + 0.5, yy + 0.5,
                    bx + 1, by + 1, bw - 2, titleH - 1, radius - 1);
                if (cov > 0) PutRgb(xx, yy, t.TitleBar, 255, cov);
            }
        FillRectRgb(bx + 1, by + titleH, bw - 2, 1, t.Divider, 255);
        for (int yy = by + 1; yy < by + titleH; yy++)
            for (int xx = bx + 1; xx < bx + Px(4); xx++)
            {
                double cov = RoundCoverage(xx + 0.5, yy + 0.5,
                    bx + 1, by + 1, bw - 2, titleH - 1, radius - 1);
                if (cov <= 0) continue;
                double tt = (yy - by) / (double)Math.Max(1, titleH - 1);
                int rgb = (Lerp(0x4F, 0x2A, tt) << 16) | (Lerp(0x5B, 0xA9, tt) << 8)
                    | Lerp(0xD5, 0xE0, tt);
                PutRgb(xx, yy, rgb, 255, cov);
            }

        int tx = bx + Px(16);
        int titleFont = Math.Max(12, Px(13));
        byte[]? titleMask = BuildTextMask(frame.Title, titleFont, 700, out int tmw, out _);
        int titleW = InkWidth(titleMask, tmw, titleFont + 6);
        DrawNoticeLine(frame.Title, tx, by + (titleH - titleFont) / 2 - Px(1),
            Px(220), titleFont, 700, t.TextPrimary
        );
        if (!string.IsNullOrEmpty(frame.Subtitle))
            DrawNoticeLine(frame.Subtitle, tx + titleW + Px(10),
                by + (titleH - Math.Max(11, Px(11))) / 2,
                bw - (tx - bx) - titleW - Px(120), Math.Max(11, Px(11)), 400, t.TextMuted);

        int btnH = Px(32);
        int closeW = Px(46);
        int closeX = bx + bw - closeW - Px(2);
        int closeY = by + (titleH - btnH) / 2;
        bool closeHot = frame.Hover == ToolHover.Close;
        bool closeDown = closeHot && frame.Pressed;
        DrawCaptionButton(closeX, closeY, closeW, btnH,
            closeHot, closeDown, t.IsHighContrast ? t.TextPrimary : 0x5B6272);
        layout.Close = (closeX, closeY, closeW, btnH);

        if (frame.Mode != ToolMode.Held)
        {
            int pinS = Px(32);
            int pinX = closeX - pinS - Px(2);
            int pinY = closeY;
            bool hot = frame.Hover == ToolHover.Pin;
            bool down = hot && frame.Pressed;
            int bg = hot ? (down ? t.ControlPressed : t.ControlHover) : t.TitleBar;
            FillRoundRectRgb(pinX, pinY, pinS, pinS, Px(4), bg, 255);
            DrawPinIcon(pinX, pinY, pinS, frame.Pinned ? t.Accent : t.TextSecondary);
            layout.Pin = (pinX, pinY, pinS, pinS);
        }

        int top = by + titleH + Px(12);
        int bottom = by + bh - Px(36);
        int left = bx + Px(14);
        int right = bx + bw - Px(14);
        int availW = right - left;
        int availH = bottom - top;
        if (availW <= 0 || availH <= 0) return;

        switch (frame.Mode)
        {
            case ToolMode.QuickLook:
                DrawQuickLook(frame, layout, left, top, availW, availH);
                break;
            case ToolMode.Markup:
                DrawMarkup(frame, layout, left, top, availW, availH);
                break;
            case ToolMode.Trim:
                DrawTrim(frame, layout, left, top, availW, availH);
                break;
            default:
                DrawHeld(frame, layout, left, top, availW, availH);
                break;
        }

        FillRectRgb(bx + 1, by + bh - Px(30), bw - 2, 1, t.Divider, 255);
        if (!string.IsNullOrEmpty(frame.Status))
            DrawNoticeLine(frame.Status, left + Px(2), by + bh - Px(21),
                availW - Px(4), Math.Max(10, Px(11)), 400, t.TextSecondary);

        if (frame.ConfirmClose) DrawConfirmOverlay(frame, layout, bx, by, bw, bh, t);
    }

    /// <summary>关闭确认：不丢内容的三选一，铺在窗口内容之上但保留标题栏。</summary>
    private void DrawConfirmOverlay(ToolFrame f, ToolLayout l, int bx, int by, int bw, int bh,
        ThemePalette t)
    {
        for (int y = by + 1; y < by + bh - 1; y++)
            for (int x = bx + 1; x < bx + bw - 1; x++)
                PutRgb(x, y, t.Scrim, t.IsDark ? 168 : 120, 1.0);

        int dw = Math.Min(Px(380), bw - Px(48));
        int dh = Px(176);
        int dx = bx + (bw - dw) / 2;
        int dy = by + (bh - dh) / 2;
        DrawSoftShadow(dx, dy, dw, dh, Px(12), t.IsHighContrast ? 0 : 90);
        FillRoundRectRgb(dx, dy, dw, dh, Px(12), t.CardBorder, 255);
        FillRoundRectRgb(dx + 1, dy + 1, dw - 2, dh - 2, Px(11), t.Window, 255);

        int font = Math.Max(12, Px(14));
        DrawTextCentered("保存标注？", dx + Px(12), dy + Px(20), dw - Px(24), font, 700,
            t.TextPrimary);
        int bodyFont = Math.Max(11, Px(12));
        DrawTextCentered("关闭后未保存的标注会丢失。可以先保存一份 PNG 副本。",
            dx + Px(16), dy + Px(48), dw - Px(32), bodyFont, 400, t.TextSecondary);

        int bwid = Px(104), bhei = Px(32), gap = Px(8);
        int rowY = dy + dh - bhei - Px(20);
        int rowX = dx + (dw - (bwid * 3 + gap * 2)) / 2;
        l.ConfirmSave = (rowX, rowY, bwid, bhei);
        l.ConfirmDiscard = (rowX + bwid + gap, rowY, bwid, bhei);
        l.ConfirmCancel = (rowX + (bwid + gap) * 2, rowY, bwid, bhei);
        DrawButton(l.ConfirmSave, "保存", ButtonStyle.Primary,
            f.Hover == ToolHover.ConfirmSave, f.Hover == ToolHover.ConfirmSave && f.Pressed, t);
        DrawButton(l.ConfirmDiscard, "丢弃", ButtonStyle.Secondary,
            f.Hover == ToolHover.ConfirmDiscard, f.Hover == ToolHover.ConfirmDiscard && f.Pressed, t);
        DrawButton(l.ConfirmCancel, "取消", ButtonStyle.Secondary,
            f.Hover == ToolHover.ConfirmCancel, f.Hover == ToolHover.ConfirmCancel && f.Pressed, t);
    }

    private enum ButtonStyle
    {
        Primary,
        Secondary,
        Danger,
    }

    private void DrawButton((int X, int Y, int W, int H) r, string label, ButtonStyle style,
        bool hover, bool pressed, ThemePalette t)
    {
        if (r.W <= 0 || r.H <= 0) return;
        int bg;
        int fg;
        if (style == ButtonStyle.Primary)
        {
            bg = pressed ? t.AccentPressed : hover ? t.AccentHover : t.Accent;
            fg = t.OnAccent;
            FillRoundRectRgb(r.X, r.Y, r.W, r.H, Px(4), bg, 255);
        }
        else
        {
            bg = pressed ? t.ControlPressed : hover ? t.ControlHover : t.Control;
            fg = style == ButtonStyle.Danger ? t.Danger : t.TextPrimary;
            FillRoundRectRgb(r.X, r.Y, r.W, r.H, Px(4), t.ControlBorder, 255);
            FillRoundRectRgb(r.X + 1, r.Y + 1, r.W - 2, r.H - 2, Px(4), bg, 255);
        }
        int font = Math.Max(11, Px(12));
        DrawTextCentered(label, r.X + Px(8), r.Y + (r.H - font) / 2, r.W - Px(16),
            font, 600, fg);
    }

    /// <summary>Win11 标题栏按钮：46×32 热区，关闭 hover 为 #C42B1C 白叉。</summary>
    private void DrawCaptionButton(int x, int y, int w, int h, bool hover, bool pressed, int fg)
    {
        ThemePalette t = Theme;
        int bg = hover
            ? (t.IsHighContrast ? t.Accent : pressed ? 0xA02318 : t.Danger)
            : t.TitleBar;
        if (hover) FillRoundRectRgb(x, y, w, h, Px(4), bg, 255);
        int color = hover ? 0xFFFFFF : fg;
        int cx = x + w / 2, cy = y + h / 2, arm = Px(5);
        int thick = Math.Max(2, Px(1));
        for (int i = -thick; i <= thick; i++)
        {
            StrokeTool(cx - arm, cy - arm + i, cx + arm, cy + arm + i, color, 1);
            StrokeTool(cx + arm, cy - arm + i, cx - arm, cy + arm + i, color, 1);
        }
    }

    private void DrawPinIcon(int x, int y, int box, int rgb)
    {
        int cx = x + box / 2, cy = y + box / 2;
        int headW = Px(11), headH = Px(3);
        FillRoundRectRgb(cx - headW / 2, cy - Px(6), headW, headH, headH / 2, rgb, 255);
        StrokeTool(cx, cy - Px(3), cx, cy + Px(2), rgb, Math.Max(2, Px(2)));
        // 针尖：从针杆底部收成一点。
        StrokeTool(cx, cy + Px(2), cx - Px(2), cy + Px(7), rgb, Math.Max(1, Px(1.4)));
        StrokeTool(cx, cy + Px(2), cx + Px(2), cy + Px(7), rgb, Math.Max(1, Px(1.4)));
    }

    /// <summary>柔阴影：从外到内叠加低 alpha 圆角矩形，边界不再出现色阶。</summary>
    private void DrawSoftShadow(int x, int y, int w, int h, int radius, int baseAlpha)
    {
        if (baseAlpha <= 0) return;
        int steps = 7;
        for (int i = steps; i >= 1; i--)
        {
            double k = i / (double)steps;
            int alpha = (int)Math.Round(baseAlpha * Math.Pow(1 - k, 2.0));
            if (alpha <= 0) continue;
            int grow = Px(i * 1.1);
            FillRoundRectRgb(x - grow, y + Px(2) + grow / 2, w + grow * 2, h + grow,
                radius + grow, 0x101828, alpha);
        }
    }

    private void FillRectRgb(int x, int y, int w, int h, int rgb, int a)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                PutRgb(xx, yy, rgb, a);
    }

    private static int InkWidth(byte[]? mask, int w, int h)
    {
        if (mask is null || w <= 0) return 0;
        int right = 0;
        int count = Math.Min(mask.Length, w * h);
        for (int i = 0; i < count; i++)
        {
            if (mask[i] <= 8) continue;
            int x = i % w;
            if (x >= right) right = x + 1;
        }
        return right;
    }

    private void DrawTextCentered(string text, int x, int y, int maxW, int fontH, int weight, int rgb)
    {
        byte[]? mask = BuildTextMask(text, fontH, weight, out int mw, out int mh);
        if (mask is null) return;
        int ink = Math.Min(InkWidth(mask, mw, mh), maxW);
        int dx = x + Math.Max(0, (maxW - ink) / 2) - Px(2);
        for (int yy = 0; yy < mh; yy++)
            for (int xx = 0; xx < ink; xx++)
            {
                int a = mask[yy * mw + xx];
                if (a > 0) PutRgb(dx + xx, y + yy, rgb, a);
            }
    }

    private (int X, int Y) ToScreen(int x, int y, (int X, int Y, int W, int H) c, double zoom,
        int panX, int panY)
    {
        if (Math.Abs(zoom - 1) < 0.001 && panX == 0 && panY == 0) return (x, y);
        double cx = c.X + c.W / 2.0, cy = c.Y + c.H / 2.0;
        return ((int)Math.Round(cx + (x - cx) * zoom + panX),
                (int)Math.Round(cy + (y - cy) * zoom + panY));
    }

    private (int X, int Y) ToBase(int x, int y, (int X, int Y, int W, int H) c, double zoom,
        int panX, int panY)
    {
        if (Math.Abs(zoom - 1) < 0.001 && panX == 0 && panY == 0) return (x, y);
        double cx = c.X + c.W / 2.0, cy = c.Y + c.H / 2.0;
        return ((int)Math.Round(cx + (x - cx - panX) / zoom),
                (int)Math.Round(cy + (y - cy - panY) / zoom));
    }

    private void DrawQuickLook(ToolFrame f, ToolLayout l, int left, int top, int w, int h)
    {
        ThemePalette t = Theme;
        int thumbH = Px(42);
        int imgH = h - thumbH - Px(16);
        var img = (left, top, w, imgH);
        DrawToolImage(f.Paths.Count > 0 ? f.Paths[f.Index] : null, img, img,
            f.Zoom, f.PanX, f.PanY);
        l.Canvas = img;

        int tw = Px(56), gap = Px(8), maxVisible = 8;
        int total = f.Paths.Count;
        int n = Math.Min(total, maxVisible);
        if (n <= 0) return;
        // 第 9 张起不再「消失」：缩略图条随当前项横向滚动（P1-9）。
        int start = total <= maxVisible ? 0 : Math.Clamp(f.Index - n / 2, 0, total - n);
        int stripW = n * tw + (n - 1) * gap;
        int sx = left + (w - stripW) / 2;
        int sy = top + imgH + Px(10);
        for (int i = 0; i < n; i++)
        {
            int idx = start + i;
            var r = (sx + i * (tw + gap), sy, tw, thumbH);
            bool selected = idx == f.Index;
            // P2-10：选中项外圈 8% 品牌色柔光 + 2 DIP 实环，比单根硬边更接近 Fluent。
            if (selected)
                FillRoundRectRgb(r.Item1 - Px(4), r.Item2 - Px(4), tw + Px(8),
                    thumbH + Px(8), Px(8), t.Accent, 26);
            FillRoundRectRgb(r.Item1 - Px(2), r.Item2 - Px(2), tw + Px(4), thumbH + Px(4),
                Px(6), selected ? t.Accent : t.Border, 255);
            DrawToolImage(f.Paths[idx], r, r, 1, 0, 0, useThemeSurface: false);
            l.Thumbs.Add(r);
            l.ThumbIndexes.Add(idx);
        }
    }

    private void DrawMarkup(ToolFrame f, ToolLayout l, int left, int top, int w, int h)
    {
        ThemePalette t = Theme;
        int toolsW = Px(56);
        int contentH = h - Px(42);
        FillRoundRectRgb(left, top, toolsW, contentH, Px(8), t.SurfaceAlt, 255);
        int bs = Px(40);
        for (int i = 0; i < 4; i++)
        {
            var r = (left + Px(8), top + Px(8) + i * (bs + Px(4)), bs, bs);
            bool on = i == f.ToolIndex;
            bool hot = f.Hover == ToolHover.Tool && f.HoverIndex == i;
            int bg = on ? t.Accent : hot ? t.ControlHover : t.SurfaceAlt;
            if (on || hot) FillRoundRectRgb(r.Item1, r.Item2, bs, bs, Px(4), bg, 255);
            DrawToolIcon(i, r, on ? t.OnAccent : t.TextSecondary);
            l.Tools.Add(r);
        }

        int cx = left + toolsW + Px(10);
        int cw = w - toolsW - Px(10);
        var canvas = (cx, top, cw, contentH);
        DrawToolImage(f.Paths.Count > 0 ? f.Paths[f.Index] : null, canvas, canvas,
            f.Zoom, f.PanX, f.PanY);
        int mark = t.IsHighContrast ? t.Accent : 0xEF8A24;
        foreach (MarkShape s in f.Shapes) DrawMarkShapeOnCanvas(s, canvas, f.Zoom, f.PanX, f.PanY, mark);
        if (f.Preview is not null)
            DrawMarkShapeOnCanvas(f.Preview, canvas, f.Zoom, f.PanX, f.PanY, mark);
        l.Canvas = canvas;

        int bhei = Px(32), bwid = Px(96);
        int by2 = top + h - bhei - Px(2);
        l.Cancel = (cx + cw - bwid * 2 - Px(8), by2, bwid, bhei);
        l.Save = (cx + cw - bwid, by2, bwid, bhei);
        DrawButton(l.Cancel, "取消", ButtonStyle.Secondary,
            f.Hover == ToolHover.Cancel, f.Hover == ToolHover.Cancel && f.Pressed, t);
        DrawButton(l.Save, "保存", ButtonStyle.Primary,
            f.Hover == ToolHover.Save, f.Hover == ToolHover.Save && f.Pressed, t);
    }

    private void DrawTrim(ToolFrame f, ToolLayout l, int left, int top, int w, int h)
    {
        ThemePalette t = Theme;
        int contentH = h - Px(44);
        var canvas = (left, top, w, contentH);
        DrawToolImage(f.Paths.Count > 0 ? f.Paths[f.Index] : null, canvas, canvas,
            f.Zoom, f.PanX, f.PanY);
        l.Canvas = canvas;

        // v1 不做视频编码：这里只预览 + 交给系统播放器，不再放一个点了没结果的「修剪」。
        int bhei = Px(32), bwid = Px(160), cancelW = Px(88);
        int by2 = top + h - bhei - Px(2);
        l.Cancel = (left, by2, cancelW, bhei);
        l.Trim = (left + w - bwid, by2, bwid, bhei);
        DrawButton(l.Cancel, "关闭", ButtonStyle.Secondary,
            f.Hover == ToolHover.Cancel, f.Hover == ToolHover.Cancel && f.Pressed, t);
        DrawButton(l.Trim, "用系统播放器打开", ButtonStyle.Primary,
            f.Hover == ToolHover.Primary, f.Hover == ToolHover.Primary && f.Pressed, t);
    }

    private void DrawHeld(ToolFrame f, ToolLayout l, int left, int top, int w, int h)
    {
        ThemePalette t = Theme;
        int contentH = h - Px(48);
        var canvas = (left, top, w, contentH);
        DrawToolImage(f.Paths.Count > 0 ? f.Paths[f.Index] : null, canvas, canvas,
            f.Zoom, f.PanX, f.PanY);
        l.Canvas = canvas;

        int bwid = Px(132), bhei = Px(32);
        int bx2 = left + (w - bwid) / 2, by2 = top + h - bhei - Px(2);
        l.PutBack = (bx2, by2, bwid, bhei);
        DrawButton(l.PutBack, "放回绳上", ButtonStyle.Primary,
            f.Hover == ToolHover.Cancel, f.Hover == ToolHover.Cancel && f.Pressed, t);
    }

    /// <summary>20 DIP 线性图标：矩形 / 椭圆 / 箭头 / 笔，替代不可控的 Unicode 字形（P2-3）。</summary>
    private void DrawToolIcon(int kind, (int X, int Y, int W, int H) r, int rgb)
    {
        int cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
        int s = Px(9);
        int thick = Math.Max(2, Px(1.7));
        switch (kind)
        {
            case 0:
                StrokeTool(cx - s, cy - s + Px(2), cx + s, cy - s + Px(2), rgb, thick);
                StrokeTool(cx + s, cy - s + Px(2), cx + s, cy + s - Px(2), rgb, thick);
                StrokeTool(cx + s, cy + s - Px(2), cx - s, cy + s - Px(2), rgb, thick);
                StrokeTool(cx - s, cy + s - Px(2), cx - s, cy - s + Px(2), rgb, thick);
                break;
            case 1:
                for (int i = 0; i < 96; i++)
                {
                    double a0 = i * Math.PI * 2 / 96, a1 = (i + 1) * Math.PI * 2 / 96;
                    StrokeTool(cx + (int)(s * Math.Cos(a0)), cy + (int)(s * 0.8 * Math.Sin(a0)),
                        cx + (int)(s * Math.Cos(a1)), cy + (int)(s * 0.8 * Math.Sin(a1)), rgb, thick);
                }
                break;
            case 2:
                StrokeTool(cx - s, cy + s, cx + s, cy - s, rgb, thick);
                StrokeTool(cx + s, cy - s, cx + s - Px(5), cy - s + Px(2), rgb, thick);
                StrokeTool(cx + s, cy - s, cx + s - Px(2), cy - s + Px(5), rgb, thick);
                break;
            default:
                // 笔：两条平行斜线构成笔身，左下平口 + 顶端短横构成笔尖/笔帽。
                StrokeTool(cx - s, cy + s, cx + s - Px(2), cy - s + Px(2), rgb, thick);
                StrokeTool(cx - s + Px(4), cy + s, cx + s + Px(2), cy - s + Px(2), rgb, thick);
                StrokeTool(cx - s, cy + s, cx - s + Px(5), cy + s, rgb, thick);
                StrokeTool(cx + s - Px(4), cy - s + Px(1), cx + s, cy - s + Px(4), rgb, thick);
                break;
        }
    }

    private void DrawMarkShapeOnCanvas(MarkShape s, (int X, int Y, int W, int H) c,
        double zoom, int panX, int panY, int rgb)
    {
        (int x0, int y0) = ToScreen(s.X0, s.Y0, c, zoom, panX, panY);
        (int x1, int y1) = ToScreen(s.X1, s.Y1, c, zoom, panX, panY);
        DrawMarkShape(new MarkShape(s.Kind, x0, y0, x1, y1), rgb, Math.Max(2, Px(2)));
    }

    private void DrawToolImage(string? path, (int X, int Y, int W, int H) r,
        (int X, int Y, int W, int H) clip, double zoom = 1, int panX = 0, int panY = 0,
        bool useThemeSurface = true)
    {
        ThemePalette t = Theme;
        if (r.W <= 2 || r.H <= 2) return;
        if (useThemeSurface)
            FillRoundRectRgb(r.X, r.Y, r.W, r.H, Px(6), t.Surface, 255);
        else
            FillRoundRectRgb(r.X, r.Y, r.W, r.H, Px(4), t.Surface, 255);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            DrawTextCentered("（无图片）", r.X + Px(16), r.Y + r.H / 2 - Px(8),
                r.W - Px(32), Math.Max(11, Px(13)), 400, t.TextMuted);
            return;
        }
        int inset = useThemeSurface ? Px(4) : 0;
        int iw = Math.Max(1, r.W - inset * 2), ih = Math.Max(1, r.H - inset * 2);
        int dw = Math.Max(1, (int)Math.Round(iw * zoom));
        int dh = Math.Max(1, (int)Math.Round(ih * zoom));
        int dx = r.X + inset + (iw - dw) / 2 + panX;
        int dy = r.Y + inset + (ih - dh) / 2 + panY;
        byte[]? pixels = _thumbs.Get(path, dw, dh);
        if (pixels is null || pixels.Length < dw * dh * 4) return;
        int radius = Math.Abs(zoom - 1) < 0.001 ? Px(useThemeSurface ? 4 : 3) : Px(4);
        for (int yy = 0; yy < dh; yy++)
            for (int xx = 0; xx < dw; xx++)
            {
                int gx = dx + xx, gy = dy + yy;
                if (gx < clip.X || gx >= clip.X + clip.W || gy < clip.Y || gy >= clip.Y + clip.H)
                    continue;
                int src = (yy * dw + xx) * 4;
                int a = pixels[src + 3];
                if (a == 0) continue;
                // 缩放后由画布圆角裁剪；1:1 时保留图片内缘圆角。
                double cov = Math.Abs(zoom - 1) < 0.001
                    ? RoundCoverage(gx + 0.5, gy + 0.5, dx, dy, dw, dh, radius)
                    : RoundCoverage(gx + 0.5, gy + 0.5, clip.X, clip.Y, clip.W, clip.H, Px(6));
                if (cov > 0)
                    PutCoverage(gx, gy, pixels[src], pixels[src + 1], pixels[src + 2], a, cov);
            }
    }

    private void DrawMarkShape(MarkShape s, int rgb, int width)
    {
        switch (s.Kind)
        {
            case 0:
                StrokeTool(s.X0, s.Y0, s.X1, s.Y0, rgb, width);
                StrokeTool(s.X1, s.Y0, s.X1, s.Y1, rgb, width);
                StrokeTool(s.X1, s.Y1, s.X0, s.Y1, rgb, width);
                StrokeTool(s.X0, s.Y1, s.X0, s.Y0, rgb, width);
                break;
            case 1:
                int cx = (s.X0 + s.X1) / 2, cy = (s.Y0 + s.Y1) / 2;
                int rx = Math.Max(1, Math.Abs(s.X1 - s.X0) / 2);
                int ry = Math.Max(1, Math.Abs(s.Y1 - s.Y0) / 2);
                for (int i = 0; i < 180; i++)
                {
                    double a0 = i * Math.PI * 2 / 180, a1 = (i + 1) * Math.PI * 2 / 180;
                    StrokeTool(cx + (int)(rx * Math.Cos(a0)), cy + (int)(ry * Math.Sin(a0)),
                        cx + (int)(rx * Math.Cos(a1)), cy + (int)(ry * Math.Sin(a1)), rgb, width);
                }
                break;
            case 2:
                StrokeTool(s.X0, s.Y0, s.X1, s.Y1, rgb, width);
                double ang = Math.Atan2(s.Y1 - s.Y0, s.X1 - s.X0);
                int len = Px(10);
                StrokeTool(s.X1, s.Y1,
                    s.X1 - (int)(len * Math.Cos(ang - 0.5)), s.Y1 - (int)(len * Math.Sin(ang - 0.5)),
                    rgb, width);
                StrokeTool(s.X1, s.Y1,
                    s.X1 - (int)(len * Math.Cos(ang + 0.5)), s.Y1 - (int)(len * Math.Sin(ang + 0.5)),
                    rgb, width);
                break;
            default:
                StrokeTool(s.X0, s.Y0, s.X1, s.Y1, rgb, width);
                break;
        }
    }

    private void StrokeTool(int x0, int y0, int x1, int y1, int rgb, int width)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        if (steps <= 0) { PutRgb(x0, y0, rgb, 255); return; }
        int half = Math.Max(0, width / 2);
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            int x = (int)Math.Round(x0 + (x1 - x0) * t);
            int y = (int)Math.Round(y0 + (y1 - y0) * t);
            for (int wy = -half; wy <= half; wy++)
                for (int wx = -half; wx <= half; wx++)
                    PutRgb(x + wx, y + wy, rgb, 255);
        }
    }

    /// <summary>导出画布的一个矩形（反预乘后写 PNG），用于「标注保存」与渲染证据。</summary>
    internal bool SavePng(string path, int x, int y, int w, int h)
    {
        if (_bits == null || w <= 0 || h <= 0) return false;
        x = Math.Clamp(x, 0, _widthPx - 1);
        y = Math.Clamp(y, 0, _heightPx - 1);
        w = Math.Clamp(w, 1, _widthPx - x);
        h = Math.Clamp(h, 1, _heightPx - y);
        byte[] bgra = new byte[w * h * 4];
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                uint c = _bits[(y + yy) * _widthPx + x + xx];
                int i = (yy * w + xx) * 4;
                int a = (int)((c >> 24) & 0xFF);
                if (a == 0) continue;
                bgra[i] = (byte)Math.Min(255, (int)(c & 0xFF) * 255 / a);
                bgra[i + 1] = (byte)Math.Min(255, (int)((c >> 8) & 0xFF) * 255 / a);
                bgra[i + 2] = (byte)Math.Min(255, (int)((c >> 16) & 0xFF) * 255 / a);
                bgra[i + 3] = (byte)a;
            }
        return GdiPlus.SaveBgraAsPng(bgra, w, h, path);
    }

    // ------------------------------------------------------------------
    // 自绘轻量通知卡（v1.2）：取代尺寸/样式完全由系统决定的 NIF_INFO 气泡。
    // ------------------------------------------------------------------

    /// <summary>#RRGGBB → DIB 的 B/G/R 参数序，避免手写反序把品牌色画错。</summary>
    private void PutRgb(int x, int y, int rgb, int a, double cov = 1) =>
        PutCoverage(x, y, rgb & 0xFF, (rgb >> 8) & 0xFF, (rgb >> 16) & 0xFF, a, cov);

    private void FillRoundRectRgb(int x, int y, int w, int h, int radius, int rgb, int a) =>
        FillRoundRect(x, y, w, h, radius, rgb & 0xFF, (rgb >> 8) & 0xFF, (rgb >> 16) & 0xFF, a);

    private static int Lerp(int from, int to, double t) =>
        (int)Math.Round(from + (to - from) * Math.Clamp(t, 0, 1));

    /// <summary>
    /// 更新面板（v1.6）：版本、渠道、进度、说明与两个动作。
    /// 取代「3.2 秒通知 + 自动重启」的黑箱流程（docs/11 P0-3 / P1-7）。
    /// </summary>
    internal void RenderUpdate(UpdateView v, UpdateLayout layout)
    {
        new Span<uint>(_bits, _widthPx * _heightPx).Clear();
        _cardRects.Clear();
        _inCard = false;
        _alphaScale = 1;
        layout.Reset();

        ThemePalette t = Theme;
        int pad = Px(8), bx = pad, by = pad;
        int bw = _widthPx - pad * 2, bh = _heightPx - pad * 2;
        int radius = Px(10);
        if (bw <= Px(200) || bh <= Px(140)) return;

        DrawSoftShadow(bx, by, bw, bh, radius, t.IsHighContrast ? 0 : 64);
        FillRoundRectRgb(bx, by, bw, bh, radius, t.Border, 255);
        FillRoundRectRgb(bx + 1, by + 1, bw - 2, bh - 2, radius - 1, t.Window, 255);

        int titleH = Px(44);
        for (int yy = by + 1; yy < by + titleH; yy++)
            for (int xx = bx + 1; xx < bx + bw - 1; xx++)
            {
                double cov = RoundCoverage(xx + 0.5, yy + 0.5,
                    bx + 1, by + 1, bw - 2, titleH - 1, radius - 1);
                if (cov > 0) PutRgb(xx, yy, t.TitleBar, 255, cov);
            }
        FillRectRgb(bx + 1, by + titleH, bw - 2, 1, t.Divider, 255);
        DrawNoticeLine("软件更新", bx + Px(20), by + Px(14), Px(140),
            Math.Max(12, Px(13)), 700, t.TextPrimary);

        int btnH = Px(32), closeW = Px(46);
        int closeX = bx + bw - closeW - Px(2);
        int closeY = by + (titleH - btnH) / 2;
        DrawCaptionButton(closeX, closeY, closeW, btnH,
            v.Hover == UpdateHover.Close, v.Hover == UpdateHover.Close && v.Pressed, 0x5B6272);
        layout.Close = (closeX, closeY, closeW, btnH);

        int left = bx + Px(24), right = bx + bw - Px(24);
        int y = by + titleH + Px(18);

        string line = v.Available.Length > 0 && v.Available != v.Current
            ? v.Current + "  →  " + v.Available
            : v.Current + "  已是最新";
        DrawNoticeLine(line, left, y, right - left, Math.Max(16, Px(20)), 600, t.TextPrimary);
        y += Px(34);

        string channel = v.Channel == "msix" ? "MSIX 安装包" : "便携版";
        int chipW = Px(channel.Length > 4 ? 92 : 72), chipH = Px(22);
        FillRoundRectRgb(left, y, chipW, chipH, Px(4), t.SurfaceAlt, 255);
        DrawTextCentered(channel, left, y + Px(4), chipW, Math.Max(10, Px(11)), 600, t.TextSecondary);
        if (v.Progress >= 0 && !v.Indeterminate)
        {
            string pct = Math.Clamp(v.Progress, 0, 100) + "%";
            DrawNoticeLine(pct, right - Px(40), y + Px(4), Px(40),
                Math.Max(10, Px(11)), 600, t.TextSecondary);
        }
        y += Px(34);

        int barH = Px(6);
        FillRoundRectRgb(left, y, right - left, barH, barH / 2, t.SurfaceAlt, 255);
        int fill = v.Indeterminate ? (right - left) / 3 : (right - left) * Math.Clamp(v.Progress, 0, 100) / 100;
        if (fill > 0)
            FillRoundRectRgb(left, y, Math.Max(barH, fill), barH, barH / 2, t.Accent, 255);
        y += Px(20);

        DrawNoticeLine(v.Message, left, y, right - left,
            Math.Max(11, Px(12)), 400, t.TextSecondary);

        int rowY = by + bh - btnH - Px(24);
        FillRectRgb(bx + 1, rowY - Px(16), bw - 2, 1, t.Divider, 255);
        int primaryW = Px(v.PrimaryWide ? 148 : 96), secondaryW = Px(96), gap = Px(8);
        DrawNoticeLine("确认后才会安装", left, rowY + Px(10),
            Math.Max(Px(60), right - left - primaryW - secondaryW - gap - Px(16)),
            Math.Max(10, Px(11)), 400, t.TextMuted);

        layout.Secondary = v.SecondaryLabel.Length > 0
            ? (right - secondaryW, rowY, secondaryW, btnH)
            : default;
        layout.Primary = (right - secondaryW - gap - primaryW, rowY, primaryW, btnH);
        if (v.SecondaryLabel.Length > 0)
            DrawButton(layout.Secondary, v.SecondaryLabel, ButtonStyle.Secondary,
                v.Hover == UpdateHover.Secondary, v.Hover == UpdateHover.Secondary && v.Pressed, t);
        DrawButton(layout.Primary, v.PrimaryLabel, ButtonStyle.Primary,
            v.Hover == UpdateHover.Primary, v.Hover == UpdateHover.Primary && v.Pressed, t);
    }

    /// <summary>
    /// 快捷键录制器（P1-5）：一个可聚焦的小面板，把「按下的组合」直接显示成大号字，
    /// 让用户看得见自己按了什么；成功/被占用都写在同一行说明里。
    /// </summary>
    internal void RenderShortcut(ShortcutView v, ShortcutLayout layout)
    {
        new Span<uint>(_bits, _widthPx * _heightPx).Clear();
        _cardRects.Clear();
        _inCard = false;
        _alphaScale = 1;
        layout.Reset();

        ThemePalette t = Theme;
        int pad = Px(8), bx = pad, by = pad;
        int bw = _widthPx - pad * 2, bh = _heightPx - pad * 2;
        int radius = Px(10);
        if (bw <= Px(200) || bh <= Px(140)) return;

        DrawSoftShadow(bx, by, bw, bh, radius, t.IsHighContrast ? 0 : 64);
        FillRoundRectRgb(bx, by, bw, bh, radius, t.Border, 255);
        FillRoundRectRgb(bx + 1, by + 1, bw - 2, bh - 2, radius - 1, t.Window, 255);

        int titleH = Px(44);
        for (int yy = by + 1; yy < by + titleH; yy++)
            for (int xx = bx + 1; xx < bx + bw - 1; xx++)
            {
                double cov = RoundCoverage(xx + 0.5, yy + 0.5,
                    bx + 1, by + 1, bw - 2, titleH - 1, radius - 1);
                if (cov > 0) PutRgb(xx, yy, t.TitleBar, 255, cov);
            }
        FillRectRgb(bx + 1, by + titleH, bw - 2, 1, t.Divider, 255);
        DrawNoticeLine("更改快捷键", bx + Px(20), by + Px(14), Px(160),
            Math.Max(12, Px(13)), 700, t.TextPrimary);

        int btnH = Px(32), closeW = Px(46);
        int closeX = bx + bw - closeW - Px(2);
        int closeY = by + (titleH - btnH) / 2;
        DrawCaptionButton(closeX, closeY, closeW, btnH,
            v.Hover == ShortcutHover.Close, v.Hover == ShortcutHover.Close && v.Pressed, t.TextSecondary);
        layout.Close = (closeX, closeY, closeW, btnH);

        int left = bx + Px(24), right = bx + bw - Px(24);
        int y = by + titleH + Px(20);

        // 大号「当前组合」框：录制中显示刚按下的候选值，未录制时显示已生效的值。
        string shown = v.Candidate is { Length: > 0 } candidate ? candidate
            : v.Current is { Length: > 0 } && v.Current != ClipLine.Domain.HotKeyModel.NoneShortcut
                ? v.Current
                : "未设置";
        int boxH = Px(58);
        FillRoundRectRgb(left, y, right - left, boxH, Px(8), t.SurfaceAlt, 255);
        FillRoundRectRgb(left, y, right - left, 1, 1, t.Divider, 255);
        FillRoundRectRgb(left, y + boxH - 1, right - left, 1, 1, t.Divider, 255);
        DrawTextCentered(shown, left, y + (boxH - Math.Max(18, Px(20))) / 2,
            right - left, Math.Max(18, Px(20)), 600,
            v.Candidate is { Length: > 0 } ? t.Accent : t.TextPrimary);
        y += boxH + Px(16);

        DrawNoticeLine("按住 Ctrl / Alt / Shift / Win 之一，再按字母或数字键", left, y,
            right - left, Math.Max(11, Px(12)), 400, t.TextMuted);
        y += Px(20);
        if (v.Message.Length > 0)
            DrawNoticeLine(v.Message, left, y, right - left,
                Math.Max(11, Px(12)), 400, t.TextSecondary);

        int rowY = by + bh - btnH - Px(20);
        FillRectRgb(bx + 1, rowY - Px(14), bw - 2, 1, t.Divider, 255);
        int primaryW = Px(88), secondaryW = Px(104), gap = Px(8);
        layout.Secondary = (left, rowY, secondaryW, btnH);
        layout.Primary = (right - primaryW, rowY, primaryW, btnH);
        DrawButton(layout.Secondary, "恢复默认", ButtonStyle.Secondary,
            v.Hover == ShortcutHover.Secondary, v.Hover == ShortcutHover.Secondary && v.Pressed, t);
        DrawButton(layout.Primary, "完成", ButtonStyle.Primary,
            v.Hover == ShortcutHover.Primary, v.Hover == ShortcutHover.Primary && v.Pressed, t);
    }

    /// <summary>
    /// 画一张 Fluent 风格通知卡：圆角白底 + 1px 描边 + 三层柔阴影 +
    /// 品牌渐变竖条 + E 双线小图标 + 抗锯齿标题/正文。整窗透明度由 Present 的
    /// SourceConstantAlpha 控制，所以内容只需在显示时渲染一次。
    /// </summary>
    internal void RenderNotice(string title, string text)
    {
        new Span<uint>(_bits, _widthPx * _heightPx).Clear();
        _cardRects.Clear();
        _inCard = false;
        _alphaScale = 1;

        ThemePalette t = Theme;
        int pad = Px(8);
        int bx = pad, by = pad;
        int bw = _widthPx - pad * 2, bh = _heightPx - pad * 2 - Px(2);
        int radius = Px(12);
        if (bw <= 0 || bh <= 0) return;

        DrawSoftShadow(bx, by, bw, bh, radius, t.IsHighContrast ? 0 : 54);
        FillRoundRectRgb(bx, by, bw, bh, radius, t.CardBorder, 255);
        FillRoundRectRgb(bx + 1, by + 1, bw - 2, bh - 2, Math.Max(1, radius - 1),
            t.CardSurface, 255);

        // 左侧品牌渐变竖条（#4F5BD5 → #2AA9E0），两端避开圆角。
        int accentW = Math.Max(2, Px(3));
        int ax = bx + Px(1);
        int ay0 = by + radius, ay1 = by + bh - radius;
        for (int y = ay0; y < ay1; y++)
        {
            double k = (y - ay0) / (double)Math.Max(1, ay1 - ay0 - 1);
            int rgb = (Lerp(0x4F, 0x2A, k) << 16) | (Lerp(0x5B, 0xA9, k) << 8) | Lerp(0xD5, 0xE0, k);
            for (int x = ax; x < ax + accentW; x++) PutRgb(x, y, rgb, 255);
        }

        int iconSize = Px(24);
        int iconX = bx + Px(14), iconY = by + Px(10);
        DrawLogoMark(iconX, iconY, iconSize);

        // 关闭按钮：24×24 热区，视觉上只画一个轻量叉，不抢标题。
        int closeS = Px(24);
        int closeX = bx + bw - closeS - Px(4), closeY = by + Px(4);
        int cx = closeX + closeS / 2, cy = closeY + closeS / 2, arm = Px(4);
        for (int i = -1; i <= 1; i++)
        {
            StrokeTool(cx - arm, cy - arm + i, cx + arm, cy + arm + i, t.TextMuted, 1);
            StrokeTool(cx + arm, cy - arm + i, cx - arm, cy + arm + i, t.TextMuted, 1);
        }

        int textX = iconX + iconSize + Px(10);
        int titleFont = Math.Max(12, Px(12));
        int bodyFont = Math.Max(11, Px(11));
        int maxW = closeX - textX - Px(6);
        DrawNoticeLine(title, textX, by + Px(8), maxW, titleFont, 700, t.TextPrimary);

        List<string> lines = WrapText(text, maxW, bodyFont, 2);
        int lineH = bodyFont + Px(4);
        int bodyTop = by + Px(25);
        for (int i = 0; i < lines.Count; i++)
            DrawNoticeLine(lines[i], textX, bodyTop + i * lineH,
                maxW, bodyFont, 400, t.TextSecondary);
    }

    /// <summary>16px E「双线」标记的放大版：上直线 + 下垂弧线 + 暖橙夹点。</summary>
    private void DrawLogoMark(int x, int y, int size)
    {
        int thick = Math.Max(2, Px(2));
        int x0 = x + Px(1), x1 = x + size - Px(1);
        FillRoundRectRgb(x0, y + Px(4), x1 - x0, thick, thick / 2, 0x4F5BD5, 255);
        for (int i = x0; i < x1; i++)
        {
            double t = (i - x0) / (double)Math.Max(1, x1 - x0 - 1);
            int sag = (int)Math.Round(Px(5) * Math.Sin(Math.PI * t));
            FillRoundRectRgb(i, y + Px(11) + sag, Math.Max(1, Px(1)), thick, thick / 2,
                0x6C63F4, 255);
        }
        int dot = Px(4);
        FillRoundRectRgb(x + size / 2 - dot / 2, y + Px(7), dot, dot, dot / 2, 0xEF8A24, 255);
    }

    private void DrawNoticeLine(string? text, int x, int y, int maxW, int fontH, int weight, int rgb)
    {
        if (string.IsNullOrEmpty(text)) return;
        byte[]? mask = BuildTextMask(text, fontH, weight, out int mw, out int mh);
        if (mask is null) return;
        int drawW = Math.Min(mw, maxW);
        for (int yy = 0; yy < mh; yy++)
            for (int xx = 0; xx < drawW; xx++)
            {
                int a = mask[yy * mw + xx];
                if (a > 0) PutRgb(x + xx, y + yy, rgb, a);
            }
    }

    private static int TextWidth(string s, int fontH)
    {
        int w = 0;
        foreach (char ch in s)
            w += ch > 0x2E80 ? fontH : (int)Math.Round(fontH * 0.58);
        return w;
    }

    /// <summary>
    /// 折行优先在中文标点后断开，保证「Ctrl+V」这类 ASCII 词不会被拦腰切成
    /// 「(Ctrl+」/「V）。」。只有单段自身超宽时才退化为逐字符。
    /// </summary>
    private static List<string> WrapText(string text, int maxW, int fontH, int maxLines)
    {
        var lines = new List<string>();
        if (maxW <= fontH || string.IsNullOrEmpty(text)) return lines;

        var segments = new List<string>();
        var seg = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            seg.Append(ch);
            if ("，。、；：！？".IndexOf(ch) >= 0)
            {
                segments.Add(seg.ToString());
                seg.Clear();
            }
        }
        if (seg.Length > 0) segments.Add(seg.ToString());

        var current = new System.Text.StringBuilder();
        int used = 0;
        foreach (string part in segments)
        {
            int w = TextWidth(part, fontH);
            if (w > maxW)
            {
                foreach (char ch in part)
                {
                    int cw = ch > 0x2E80 ? fontH : (int)Math.Round(fontH * 0.58);
                    if (used + cw > maxW && current.Length > 0) Flush();
                    current.Append(ch);
                    used += cw;
                }
                continue;
            }
            if (used + w > maxW && current.Length > 0) Flush();
            current.Append(part);
            used += w;
        }
        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count > maxLines)
        {
            string last = lines[maxLines - 1];
            lines[maxLines - 1] = last[..Math.Max(0, last.Length - 1)] + "…";
            lines.RemoveRange(maxLines, lines.Count - maxLines);
        }
        return lines;

        void Flush()
        {
            lines.Add(current.ToString());
            current.Clear();
            used = 0;
        }
    }

    /// <summary>
    /// 用 GDI TextOutW + ANTIALIASED_QUALITY 生成灰度文字蒙版。
    /// GDI 写 32bpp DIB 时 alpha 恒为 0，必须单独出蒙版再按覆盖率合成。
    /// </summary>
    /// <summary>
    /// P2-4：按内容选字面。纯 ASCII 走 Segoe UI（Win11 的西文 UI 字体），
    /// 含中文走 Microsoft YaHei UI（雅黑 UI 变体，字距比旧 YaHei 紧、更像系统界面）。
    /// GDI 的 ANTIALIASED_QUALITY 是灰度抗锯齿，透明蒙版只能这么做（ClearType 会带彩边）。
    /// </summary>
    private static string FaceFor(string text)
    {
        foreach (char ch in text)
            if (ch > 0x2E80) return "Microsoft YaHei UI";
        return "Segoe UI";
    }

    internal static byte[]? BuildTextMask(string text, int fontH, int weight,
        out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(text) || fontH <= 0) return null;
        int estimate = 4;
        foreach (char ch in text)
            estimate += ch > 0x2E80 ? fontH : (int)Math.Round(fontH * 0.58);
        int bw = estimate + 4, bh = fontH + 6;
        width = bw;
        height = bh;

        IntPtr dc = IntPtr.Zero, dib = IntPtr.Zero, oldBmp = IntPtr.Zero;
        IntPtr font = IntPtr.Zero, oldFont = IntPtr.Zero;
        try
        {
            var header = new ShellNative.BITMAPINFOHEADER
            {
                BiSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.BITMAPINFOHEADER>(),
                BiWidth = bw,
                BiHeight = -bh,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0,
            };
            dc = ShellNative.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            dib = ShellNative.CreateDIBSection(dc, ref header, ShellNative.DibRgbColors,
                out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            oldBmp = ShellNative.SelectObject(dc, dib);
            new Span<uint>((void*)bits, bw * bh).Clear();

            font = ShellNative.CreateFontW(-fontH, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0,
                FaceFor(text));
            if (font == IntPtr.Zero) return null;
            oldFont = ShellNative.SelectObject(dc, font);
            ShellNative.SetBkMode(dc, 1);
            ShellNative.SetTextColor(dc, 0x00FFFFFFu);
            ShellNative.TextOutW(dc, 2, 2, text, text.Length);

            byte[] mask = new byte[bw * bh];
            uint* pixels = (uint*)bits;
            for (int i = 0; i < mask.Length; i++)
            {
                uint v = pixels[i];
                int a = Math.Max((int)(v & 0xFF),
                    Math.Max((int)((v >> 8) & 0xFF), (int)((v >> 16) & 0xFF)));
                if (a > 0) mask[i] = (byte)a;
            }
            return mask;
        }
        catch { return null; }
        finally
        {
            if (oldFont != IntPtr.Zero) ShellNative.SelectObject(dc, oldFont);
            if (font != IntPtr.Zero) ShellNative.DeleteObject(font);
            if (oldBmp != IntPtr.Zero) ShellNative.SelectObject(dc, oldBmp);
            if (dib != IntPtr.Zero) ShellNative.DeleteObject(dib);
            if (dc != IntPtr.Zero) ShellNative.DeleteDC(dc);
        }
    }

    /// <summary>把当前帧导出成 PNG，供冒烟/验证作为确定性渲染证据。</summary>
    internal bool SavePng(string path) =>
        GdiPlus.SavePremultipliedFrame((IntPtr)_bits, _widthPx, _heightPx, path);

    /// <summary>
    /// pptDst 会同时决定分层窗口在屏幕上的位置；默认 (0,0) 供全屏绳/掉落层使用。
    /// 需要居中的工具窗口、贴右下角的通知卡必须显式传入自己的位置，否则每次
    /// Present 都会把窗口移回屏幕左上角。
    /// </summary>
    internal bool Present(IntPtr hWnd, byte alpha = 255, int dstX = 0, int dstY = 0)
    {
        var dst = new ShellNative.POINT(dstX, dstY);
        var size = new ShellNative.SIZE(_widthPx, _heightPx);
        var src = new ShellNative.POINT(0, 0);
        var blend = new ShellNative.BLENDFUNCTION
        {
            BlendOp = ShellNative.AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
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

/// <summary>快捷键录制器上的可交互元素（P1-5）。</summary>
internal enum ShortcutHover
{
    None,
    Close,
    Primary,
    Secondary,
}

/// <summary>录制器一帧内容：当前值 + 刚按下但尚未生效的组合 + 一行结果说明。</summary>
internal sealed record ShortcutView(
    string Current,
    string? Candidate,
    string Message,
    ShortcutHover Hover = ShortcutHover.None,
    bool Pressed = false);

internal sealed class ShortcutLayout
{
    public (int X, int Y, int W, int H) Close;
    public (int X, int Y, int W, int H) Primary;
    public (int X, int Y, int W, int H) Secondary;

    public void Reset() => Close = Primary = Secondary = default;
}

/// <summary>更新面板上的可交互元素。</summary>
internal enum UpdateHover
{
    None,
    Close,
    Primary,
    Secondary,
}

/// <summary>更新面板的一帧内容；由壳层从 UpdateSnapshot 映射，绘制层不依赖应用层。</summary>
internal sealed record UpdateView(
    string Current,
    string Available,
    string Channel,
    string Message,
    int Progress,
    bool Indeterminate,
    string PrimaryLabel,
    string SecondaryLabel,
    bool PrimaryWide = false,
    UpdateHover Hover = UpdateHover.None,
    bool Pressed = false);

internal sealed class UpdateLayout
{
    public (int X, int Y, int W, int H) Close;
    public (int X, int Y, int W, int H) Primary;
    public (int X, int Y, int W, int H) Secondary;

    public void Reset() => Close = Primary = Secondary = default;
}

/// <summary>
/// 一张可见卡片的最小渲染信息；Copied 来自门面 ItemSnapshot.CopiedBadge，
/// SwingDegrees（含静态倾角）与 DropDip 来自 Domain 层的 RopeMotion 摆动模型。
/// </summary>
public sealed record CardVisual(Guid Id, string Path, bool Kept, bool Copied = false,
    double SwingDegrees = 0, double DropDip = 0, double Tilt = 0);

/// <summary>掉落层的一张卡片；Progress = 已播放时长 / 总时长，0→1。</summary>
public sealed record FallCard(string Path, int X, int Y, int W, int H,
    double Tilt, bool Kept, double Progress);

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
