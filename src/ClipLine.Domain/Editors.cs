namespace ClipLine.Domain;

// ---------------------------------------------------------------- 快速查看（E-05 / K-04）

public enum QuickLookAction
{
    Toggle,
    Next,
    Previous,
    Close,
}

/// <summary>K-04：← → 浏览整条绳上的所有项目，Space/Esc 关闭。</summary>
public sealed class QuickLookModel
{
    public int Index { get; private set; }
    public bool IsOpen { get; private set; }
    public int Total { get; private set; }

    public void Open(int index, int total)
    {
        Total = Math.Max(0, total);
        Index = Total == 0 ? 0 : Math.Clamp(index, 0, Total - 1);
        IsOpen = Total > 0;
    }

    public void Apply(QuickLookAction action, int total)
    {
        Total = Math.Max(0, total);
        switch (action)
        {
            case QuickLookAction.Close: IsOpen = false; break;
            case QuickLookAction.Toggle: IsOpen = !IsOpen; break;
            case QuickLookAction.Next when Total > 0: Index = (Index + 1) % Total; break;
            case QuickLookAction.Previous when Total > 0: Index = (Index - 1 + Total) % Total; break;
        }
    }
}

// ---------------------------------------------------------------- 标注（E-01 / E-02）

public enum MarkupTool
{
    Pen,
    Arrow,
    Rectangle,
    Text,
    Mosaic,
    Crop,
}

public sealed record MarkupStroke(
    MarkupTool Tool,
    IReadOnlyList<(double X, double Y)> Points,
    string Color,
    double Width);

public sealed record MarkupDocument(string Path, string SourceFormat, IReadOnlyList<MarkupStroke> Strokes);

public static class MarkupModel
{
    /// <summary>E-01：Markup 默认吐出压缩率很低的 PNG，写回前必须按原格式重新编码控制体积。</summary>
    public static bool NeedsRecode(string sourceFormat) =>
        !string.Equals(Normalize(sourceFormat), "png", StringComparison.OrdinalIgnoreCase);

    public static string TargetFormat(string sourceFormat) => Normalize(sourceFormat);

    public static string Normalize(string format)
    {
        string value = (format ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return value switch
        {
            "jpeg" or "jpg" => "jpeg",
            "tiff" or "tif" => "tiff",
            "heif" or "heic" => "heic",
            "webp" or "gif" or "bmp" or "png" => value,
            _ => "png",
        };
    }

    public static void Validate(MarkupDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Path)) throw new ArgumentException("缺少目标文件", nameof(document));
        foreach (var stroke in document.Strokes)
        {
            if (stroke.Width <= 0 || stroke.Width > 100) throw new ArgumentOutOfRangeException(nameof(document), "笔宽越界");
            if (stroke.Points.Count == 0) throw new ArgumentException("笔画没有点", nameof(document));
        }
    }
}

// ---------------------------------------------------------------- 录屏剪辑（E-03）

public sealed record TrimRange(long StartMs, long EndMs, long DurationMs)
{
    public bool IsValid => StartMs >= 0 && EndMs > StartMs && EndMs <= DurationMs;
    public long LengthMs => EndMs - StartMs;
}

public static class TrimModel
{
    public const int MaxWindowWidth = 720;
    public const int MaxWindowHeight = 520;

    /// <summary>E-03：导出到同目录隐藏临时文件，watcher 会忽略，完成后原子替换。</summary>
    public static string TempFileName(string extension) =>
        ".trimming-" + Guid.NewGuid().ToString("N") + "." + extension.TrimStart('.');

    public static bool ShouldIgnoreByWatcher(string fileName) =>
        fileName.StartsWith(".trimming-", StringComparison.OrdinalIgnoreCase);

    public static TrimRange Clamp(TrimRange range)
    {
        long duration = Math.Max(0, range.DurationMs);
        long start = Math.Clamp(range.StartMs, 0, duration);
        long end = Math.Clamp(range.EndMs, start, duration);
        if (end <= start && duration > 0) end = Math.Min(duration, start + 1);
        return new TrimRange(start, end, duration);
    }

    /// <summary>窗口按视频比例，最大 720×520（E-03）。</summary>
    public static (int Width, int Height) WindowSize(int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return (MaxWindowWidth, MaxWindowHeight);
        double scale = Math.Min((double)MaxWindowWidth / videoWidth, (double)MaxWindowHeight / videoHeight);
        scale = Math.Min(scale, 1.0);
        return (Math.Max(1, (int)Math.Round(videoWidth * scale)), Math.Max(1, (int)Math.Round(videoHeight * scale)));
    }
}

// ---------------------------------------------------------------- 拿在手上（D-04 / D-05）

public static class HeldWindowModel
{
    public const double VisibleAreaRatio = 0.70;
    public const int MinSide = Layout.HeldMinSide;

    public static double ClampScale(double scale) =>
        Math.Clamp(scale, Layout.HeldMinScale, Layout.HeldMaxScale);

    /// <summary>初始大小为可见区域 70% 以内并居中。</summary>
    public static (int Width, int Height) InitialSize(int imageWidth, int imageHeight, int visibleWidth, int visibleHeight)
    {
        double maxW = visibleWidth * VisibleAreaRatio;
        double maxH = visibleHeight * VisibleAreaRatio;
        double scale = Math.Min(1.0, Math.Min(maxW / Math.Max(1, imageWidth), maxH / Math.Max(1, imageHeight)));
        return (Math.Max(MinSide, (int)Math.Round(imageWidth * scale)),
                Math.Max(MinSide, (int)Math.Round(imageHeight * scale)));
    }

    /// <summary>每开一个右下错开 24 DIP，8 个一循环。</summary>
    public static (int Dx, int Dy) CascadeOffset(int ordinal)
    {
        int step = ((ordinal % Layout.HeldOffsetCycle) + Layout.HeldOffsetCycle) % Layout.HeldOffsetCycle;
        return (step * Layout.HeldOffsetDip, step * Layout.HeldOffsetDip);
    }

    public static (int Width, int Height) ApplyScale(int width, int height, double scale)
    {
        double clamped = ClampScale(scale);
        int w = (int)Math.Round(width * clamped);
        int h = (int)Math.Round(height * clamped);
        return (Math.Max(MinSide, w), Math.Max(MinSide, h));
    }
}

// ---------------------------------------------------------------- 资源管理器右键服务（X-01 / X-02）

public static class ShellExtensionModel
{
    /// <summary>X-01：只接图片，去重；一个都没成功则蜂鸣。X-02：手动挂上的项永远固定。</summary>
    public static IReadOnlyList<string> Accept(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<string>();
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!CaptureIntegrity.IsImage(path)) continue;
            if (seen.Add(Line.NormalizePath(path))) accepted.Add(path);
        }
        return accepted;
    }

    public static bool ShouldBeep(int acceptedCount) => acceptedCount == 0;
}
