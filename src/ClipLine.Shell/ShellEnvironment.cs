using ClipLine.Application;
using ClipLine.Platform;
using Microsoft.Win32;

namespace ClipLine.Shell;

/// <summary>
/// 进程级 DPI 感知。必须在任何 WinUI / XAML 初始化之前调用：
/// self-contained 生成的清单只注册 WinRT 类、不含 DPI 声明，
/// 等 Application.Start 之后再设就来不及了（此时线程上下文已被 XAML 接管）。
/// </summary>
public static class ShellDpi
{
    /// <summary>PER_MONITOR_AWARE_V2 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (-4)。</summary>
    public static bool EnsurePerMonitorV2() =>
        ShellNative.SetProcessDpiAwarenessContext(new IntPtr(-4));

    public static uint SystemDpi => ShellNative.GetDpiForSystem();
}

/// <summary>真实时钟。</summary>
public sealed class SystemClock : IClock
{
    public long NowMs => Environment.TickCount64;
}

/// <summary>
/// 真实环境（docs/03 §1/§16 的 v0 落地）：主屏 rcWork + 单一系统缩放；
/// 主题读注册表 Personalize；减少动效读 SPI_GETCLIENTAREAANIMATION；高对比度读 SPI_GETHIGHCONTRAST。
/// 全屏检测 v0 恒 false，SHQueryUserNotificationState 接入见 docs/06 待办。
/// </summary>
public class ShellEnvironment : IEnvironment
{
    private readonly double _scale;

    public ShellEnvironment(double scale, string? ownDirectory = null, string? screenshotsFolder = null)
    {
        _scale = scale <= 0 ? 1.0 : scale;
        OwnDirectory = ownDirectory ?? PlatformCommandSink.OwnDirectory;
        ScreenshotsFolder = screenshotsFolder ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    public static ShellEnvironment CreateDefault() =>
        new(ShellNative.GetDpiForSystem() / 96.0);

    public virtual int ScreenWidthDip => DisplayInfo.PrimaryWidthDip();
    public virtual double DisplayScale => _scale;
    public virtual bool IsFullScreenAppActive => false; // v0 未接 SHQueryUserNotificationState
    public virtual bool ReduceMotion
    {
        get
        {
            bool enabled = true;
            return ShellNative.SystemParametersInfoW(ShellNative.SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0)
                && !enabled;
        }
    }

    public virtual bool IsDarkTheme
    {
        get
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int i && i == 0;
        }
    }

    public virtual bool IsHighContrast
    {
        get
        {
            var hc = new ShellNative.HIGHCONTRASTW
            {
                CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.HIGHCONTRASTW>(),
            };
            return ShellNative.SystemParametersInfoW(ShellNative.SPI_GETHIGHCONTRAST, hc.CbSize, ref hc, 0)
                && (hc.DwFlags & ShellNative.HCF_HIGHCONTRASTON) != 0;
        }
    }

    public virtual bool FileExists(string path) => File.Exists(path);
    public virtual bool CanDecode(string path) => Domain.CaptureIntegrity.IsSupported(path);
    public string OwnDirectory { get; }
    public string ScreenshotsFolder { get; }
    public virtual string Version => Domain.BuildInfo.Version;
    /// <summary>MSIX 包与便携版唯一的形态差异：包内根目录带 AppxManifest.xml。</summary>
    public virtual bool IsPackaged => File.Exists(
        Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml"));
    public virtual double SystemDoubleClickIntervalMs => ShellNative.GetDoubleClickTime();
}
