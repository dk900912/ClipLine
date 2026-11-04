namespace ClipLine.Domain;

public enum CaptureSource
{
    /// <summary>%LOCALAPPDATA%\ClipLine\Screenshots（F-03）。</summary>
    OwnScreenshots,

    /// <summary>自有录屏目录 / Videos\Captures（F-06）。</summary>
    OwnCaptures,

    /// <summary>桌面，仅接受带系统截图标记的真截图（F-04 / F-06）。</summary>
    Desktop,

    /// <summary>系统默认截图目录（N-03：Windows 无系统级接管，改为监视）。</summary>
    DefaultScreenshots,

    /// <summary>用户额外监视的文件夹，最多 3 个（F-10）。</summary>
    WatchFolder,

    /// <summary>剪贴板复制的图片（C-01…C-07）。</summary>
    Clipboard,
}

public sealed record CaptureCandidate(
    string Path,
    CaptureSource Source,
    long CreatedAtMs,
    bool HasScreenCaptureMark = false,
    bool IsDesktopScreenshot = false);

/// <summary>F-03…F-07、F-09、F-13、N-03：三层捕获的准入规则。</summary>
public static class CaptureRules
{
    public const long DebounceMs = 200;
    public const long StartupCutoffMs = 0;

    public const string OwnDirectoryTemplate = @"%LOCALAPPDATA%\ClipLine\Screenshots";
    public const string DefaultScreenshotsTemplate = @"%USERPROFILE%\Pictures\Screenshots";
    public const string CapturesTemplate = @"%USERPROFILE%\Videos\Captures";

    public static bool Accept(CaptureCandidate candidate, long appStartedAtMs = StartupCutoffMs)
    {
        if (string.IsNullOrWhiteSpace(candidate.Path)) return false;
        if (!CaptureIntegrity.IsSupported(candidate.Path)) return false;
        if (candidate.CreatedAtMs < appStartedAtMs) return false; // F-05：启动前的文件只登记不挂

        bool isVideo = CaptureIntegrity.IsVideo(candidate.Path);
        if (isVideo && candidate.Source is not (CaptureSource.OwnScreenshots or CaptureSource.OwnCaptures))
            return false; // F-06：录屏只在自有目录接受

        if (candidate.Source == CaptureSource.Desktop && !candidate.HasScreenCaptureMark)
            return false; // F-04/F-06：桌面只认真截图

        return true;
    }

    public static ItemOrigin ToOrigin(CaptureSource source) => source switch
    {
        CaptureSource.Clipboard => ItemOrigin.Clipboard,
        CaptureSource.WatchFolder or CaptureSource.Desktop or CaptureSource.DefaultScreenshots => ItemOrigin.WatchedFolder,
        _ => ItemOrigin.Screenshot,
    };
}
