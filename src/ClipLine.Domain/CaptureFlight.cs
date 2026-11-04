namespace ClipLine.Domain;

/// <summary>V-26 / V-27：飞入动画参数；渲染层照此构造 Composition 关键帧。</summary>
public sealed record FlightPlan(
    double DurationMs,
    double ArcLiftDip,
    int FrameRate,
    int ThumbnailLimit,
    double GlassFadeStart,
    bool Skipped);

public sealed record FallPlan(double DistanceDip, double DurationMs, double AngleDegrees, bool Skipped);

public static class CaptureFlight
{
    public const double DurationMs = 650;
    public const double ArcLiftDip = 30;
    public const int FrameRate = 120;
    public const double GlassFadeStart = 0.35;
    public const double FallDistanceDip = 520;
    public const double FallDurationMs = 550;

    public static FlightPlan Plan(int captureLongestEdge, double displayScale, bool reduceMotion, int activeFlights)
    {
        bool skipped = reduceMotion || activeFlights >= Layout.MaxConcurrentFlights;
        return new FlightPlan(
            DurationMs,
            ArcLiftDip,
            FrameRate,
            Layout.FlightThumbnailLimit(captureLongestEdge, displayScale),
            GlassFadeStart,
            skipped);
    }

    /// <summary>V-28：丢弃下落。减少动态效果时直接离场。</summary>
    public static FallPlan Fall(double tilt, bool reduceMotion) => new(
        FallDistanceDip,
        FallDurationMs,
        Layout.FallTilt(tilt),
        reduceMotion);
}
