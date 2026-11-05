namespace ClipLine.Domain;

/// <summary>
/// 所有几何与时序常量集中在此，数值直接取自 v1.8.1 源码，不做「差不多」的改写。
/// 长度单位统一为 DIP（Windows）／pt（macOS），一一对应。
/// </summary>
public static class Layout
{
    public const int RopeTop = 10;
    public const int BaseCardWidth = 150;
    public const int BasePhotoMaxHeight = 104;
    public const int CardGap = 24;
    public const int PanelExtra = 106;
    public const double PinAbove = 9.5;
    public const int HitPadding = 4;
    public const int EdgeFade = 90;
    public const int ThumbnailBase = 320;
    public const int MinVisibleCount = 3;
    public const int MaxVisibleCount = 12;
    public const int RevealBandMinDip = 8;
    public const int RevealBandMaxDip = 12;
    public const int RevealBandDip = 10;
    public const double MinTilt = -2.5;
    public const double MaxTilt = 2.5;
    public const int MaxConcurrentFlights = 2;

    // 状态机时序（S-03 / S-04 / S-07 / S-08 / V-05 / S-11 / V-37 / G-08 / A-07）
    public const long RevealDelayMs = 250;
    public const long RetractDelayMs = 500;
    public const long PeekMs = 2500;
    public const long EmptyHideMs = 700;
    public const long DestroyDelayMs = 300;
    public const long FullScreenRecheckMs = 800;
    public const long GapCloseMs = 600;
    public const long DragRecheckMs = 600;
    public const long WelcomePinMs = 5000;
    public const long InboxOfferDelayMs = 1200;
    public const long BatchStepMs = 60;
    public const long CopiedBadgeMs = 1200;
    public const long LongPressMs = 450;
    public const long FlightMs = 650;
    public const long FallMs = 550;

    public static double Scale(CardSize size) => size switch
    {
        CardSize.Small => 0.8,
        CardSize.Medium => 1.0,
        CardSize.Large => 1.35,
        _ => 1.0,
    };

    // Swift 的 round() 是「四舍五入远离零」，.NET 默认 banker's rounding，必须显式指定。
    public static int CardWidth(CardSize size) =>
        (int)Math.Round(BaseCardWidth * Scale(size), MidpointRounding.AwayFromZero);

    public static int PhotoMaxHeight(CardSize size) =>
        (int)Math.Round(BasePhotoMaxHeight * Scale(size), MidpointRounding.AwayFromZero);

    public static int Spacing(CardSize size) => CardWidth(size) + CardGap;

    public static int PanelHeight(CardSize size) => PhotoMaxHeight(size) + PanelExtra;

    /// <summary>V-31：clamp(3, 12, floor((屏宽 − 200) / spacing))。</summary>
    public static int VisibleCount(int screenWidthDip, CardSize size) =>
        Math.Clamp((int)Math.Floor((screenWidthDip - 200.0) / Spacing(size)), MinVisibleCount, MaxVisibleCount);

    /// <summary>V-32：0 表示「放得下的数量」。</summary>
    public static int MaxItems(int fits, int keepOnLine) => Math.Max(fits, keepOnLine);

    /// <summary>V-07：下垂量 sag = min(30, 宽 × 0.018)。</summary>
    public static double Sag(double width) => Math.Min(30.0, width * 0.018);

    /// <summary>V-07：ropeY(x) = 10 + 4·sag·(x/w)·(1 − x/w)。</summary>
    public static double RopeY(double x, double width)
    {
        if (width <= 0) return RopeTop;
        double t = x / width;
        return RopeTop + 4 * Sag(width) * t * (1 - t);
    }

    /// <summary>V-35：滚动上限。</summary>
    public static int ScrollMax(int count, int visibleCount, int spacing) =>
        Math.Max(0, (count - visibleCount) * spacing);

    /// <summary>V-36：靠边渐隐。</summary>
    public static double EdgeOpacity(double x, double width)
    {
        if (width <= 0) return 0;
        return Math.Clamp(Math.Min(x, width - x) / EdgeFade, 0.0, 1.0);
    }

    /// <summary>D-10：缩略图上限 320 × size.scale。</summary>
    public static int ThumbnailLimit(CardSize size) =>
        (int)Math.Round(ThumbnailBase * Scale(size), MidpointRounding.AwayFromZero);

    /// <summary>V-27：飞入缩略图 min(1500, max(400, 捕获区域最长边 × 缩放因子))。</summary>
    public static int FlightThumbnailLimit(int captureLongestEdge, double scale) =>
        Math.Clamp((int)Math.Round(captureLongestEdge * scale), 400, 1500);

    /// <summary>
    /// V-28：丢弃下落的终态倾角。Swift 源码里 e 是进度三次方 raw³（不是自然常数）：
    /// angle = tilt + (tilt×7 + 20)·e，raw=1 时 e=1，故终值 = tilt×8 + 20。
    /// </summary>
    public static double FallTilt(double tilt) => tilt * 8 + 20;

    /// <summary>G-06：拖拽阈值。</summary>
    public const double DragThresholdDip = 4;

    /// <summary>D-04：拿在手上缩放范围与错开量。</summary>
    public const double HeldMinScale = 0.1;
    public const double HeldMaxScale = 4.0;
    public const int HeldMinSide = 60;
    public const int HeldOffsetDip = 24;
    public const int HeldOffsetCycle = 8;
}
