using Microsoft.Win32;

namespace ClipLine.Shell;

/// <summary>
/// 一套绘制用的语义色板。工具窗、通知卡、更新面板与卡片都从这里取色，
/// 不再各自硬编码 #FFFFFF / #252A34（docs/11 P1-1）。
/// </summary>
internal readonly record struct ThemePalette(
    bool IsDark,
    bool IsHighContrast,
    int Window,
    int TitleBar,
    int Border,
    int Divider,
    int TextPrimary,
    int TextSecondary,
    int TextMuted,
    int Surface,
    int SurfaceAlt,
    int Control,
    int ControlHover,
    int ControlPressed,
    int ControlBorder,
    int Accent,
    int AccentHover,
    int AccentPressed,
    int OnAccent,
    int Danger,
    int Scrim,
    int CardSurface,
    int CardBorder)
{
    public static ThemePalette Light { get; } = new(
        false, false,
        Window: 0xFFFFFF,
        TitleBar: 0xF8F9FC,
        Border: 0xD8DCE6,
        Divider: 0xE5E7EB,
        TextPrimary: 0x252A34,
        TextSecondary: 0x6B7280,
        TextMuted: 0x8A93A6,
        Surface: 0xF3F4F8,
        SurfaceAlt: 0xF5F6FA,
        Control: 0xFFFFFF,
        ControlHover: 0xF3F4F8,
        ControlPressed: 0xE9EBF2,
        ControlBorder: 0xD8DCE6,
        Accent: 0x4F5BD5,
        AccentHover: 0x3F4AC4,
        AccentPressed: 0x3540B0,
        OnAccent: 0xFFFFFF,
        Danger: 0xC42B1C,
        Scrim: 0x0B1220,
        CardSurface: 0xFFFFFF,
        CardBorder: 0xE5E7EB);

    public static ThemePalette Dark { get; } = new(
        true, false,
        Window: 0x202020,
        TitleBar: 0x272727,
        Border: 0x3A3A3A,
        Divider: 0x333333,
        TextPrimary: 0xF3F3F3,
        TextSecondary: 0xC8C8C8,
        TextMuted: 0x9A9A9A,
        Surface: 0x2B2B2B,
        SurfaceAlt: 0x303030,
        Control: 0x2D2D2D,
        ControlHover: 0x383838,
        ControlPressed: 0x242424,
        ControlBorder: 0x454545,
        Accent: 0x7B86FF,
        AccentHover: 0x8F98FF,
        AccentPressed: 0x6A75F0,
        OnAccent: 0x0E1020,
        Danger: 0xFF5A4E,
        Scrim: 0x000000,
        CardSurface: 0x2B2B2B,
        CardBorder: 0x454545);

    /// <summary>
    /// 高对比度直接用系统色，保证与「轻松使用」里的主题一致。
    /// 每次现算而不是静态缓存：高对比度配色方案可以在会话中切换，
    /// 缓存住会让「换配色方案」看起来没反应。
    /// </summary>
    public static ThemePalette HighContrastPalette() => new(
        false, true,
        Window: ShellNative.GetSysColor(ShellNative.ColorWindow),
        TitleBar: ShellNative.GetSysColor(ShellNative.ColorBtnFace),
        Border: ShellNative.GetSysColor(ShellNative.ColorWindowFrame),
        Divider: ShellNative.GetSysColor(ShellNative.ColorBtnShadow),
        TextPrimary: ShellNative.GetSysColor(ShellNative.ColorWindowText),
        TextSecondary: ShellNative.GetSysColor(ShellNative.ColorWindowText),
        TextMuted: ShellNative.GetSysColor(ShellNative.ColorGrayText),
        Surface: ShellNative.GetSysColor(ShellNative.ColorWindow),
        SurfaceAlt: ShellNative.GetSysColor(ShellNative.ColorBtnFace),
        Control: ShellNative.GetSysColor(ShellNative.ColorBtnFace),
        ControlHover: ShellNative.GetSysColor(ShellNative.ColorBtnHighlight),
        ControlPressed: ShellNative.GetSysColor(ShellNative.ColorBtnShadow),
        ControlBorder: ShellNative.GetSysColor(ShellNative.ColorWindowFrame),
        Accent: ShellNative.GetSysColor(ShellNative.ColorHighlight),
        AccentHover: ShellNative.GetSysColor(ShellNative.ColorHighlight),
        AccentPressed: ShellNative.GetSysColor(ShellNative.ColorHighlight),
        OnAccent: ShellNative.GetSysColor(ShellNative.ColorHighlightText),
        Danger: ShellNative.GetSysColor(ShellNative.ColorHighlight),
        Scrim: ShellNative.GetSysColor(ShellNative.ColorWindowText),
        CardSurface: ShellNative.GetSysColor(ShellNative.ColorWindow),
        CardBorder: ShellNative.GetSysColor(ShellNative.ColorWindowFrame));

    /// <summary>当前系统外观；进程启动读一次，WM_SETTINGCHANGE 时刷新。</summary>
    public static ThemePalette Current { get; private set; } = Resolve();

    public static ThemePalette Resolve()
    {
        if (ThemeProbe.IsHighContrast()) return HighContrastPalette();
        return ThemeProbe.AppsUseLightTheme() ? Light : Dark;
    }

    /// <summary>系统主题变化后重读；返回值表示色板是否真的变了。</summary>
    public static bool Refresh()
    {
        ThemePalette next = Resolve();
        if (next.Equals(Current)) return false;
        Current = next;
        return true;
    }
}

internal static class ThemeProbe
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    internal static bool AppsUseLightTheme()
    {
        try
        {
            object? value = Registry.CurrentUser.OpenSubKey(PersonalizeKey)?.GetValue("AppsUseLightTheme");
            return value is not int i || i != 0;
        }
        catch
        {
            // 注册表不可读时按浅色处理，与 Windows 默认一致。
            return true;
        }
    }

    internal static bool IsHighContrast()
    {
        var hc = new ShellNative.HIGHCONTRASTW
        {
            CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.HIGHCONTRASTW>(),
        };
        return ShellNative.SystemParametersInfoW(ShellNative.SPI_GETHIGHCONTRAST, hc.CbSize, ref hc, 0)
            && (hc.DwFlags & ShellNative.HCF_HIGHCONTRASTON) != 0;
    }
}

/// <summary>
/// 系统绘制的浅色控件（右键菜单、菜单圆角、滚动条）跟随深浅色。
/// uxtheme 只在私有序号上导出这两个入口，因此调用失败必须静默降级：
/// 最坏情况是「菜单还是浅色」，绝不能因为外观增强让常驻进程崩掉。
/// </summary>
internal static class ThemeHook
{
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    private static bool s_applied;

    internal static void Apply()
    {
        try
        {
            // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight。
            // 走高对比度时交还系统默认，避免把系统配色顶掉。
            int mode = ThemePalette.Current.IsHighContrast
                ? 0
                : ThemePalette.Current.IsDark ? 2 : 3;
            SetPreferredAppMode(mode);
            FlushMenuThemes();
            s_applied = true;
        }
        catch
        {
            s_applied = false;
        }
    }

    /// <summary>是否成功接管系统菜单外观（冒烟用来记录而不做硬断言）。</summary>
    internal static bool Applied => s_applied;
}

/// <summary>
/// 窗口圆角与深色边框（P2-6）。内容仍是 GDI 自绘，这里只让 DWM 侧的圆角裁剪、
/// Alt+Tab 缩略图与系统绘制的上下文菜单跟主题一致；调用失败静默忽略。
/// </summary>
internal static class WindowChrome
{
    internal static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int round = ShellNative.DwmwcpRound;
            ShellNative.DwmSetWindowAttribute(hwnd, ShellNative.DwmwaWindowCornerPreference,
                ref round, sizeof(int));
            int dark = ThemePalette.Current.IsDark ? 1 : 0;
            ShellNative.DwmSetWindowAttribute(hwnd, ShellNative.DwmwaUseImmersiveDarkMode,
                ref dark, sizeof(int));
        }
        catch
        {
            // 老版本 Windows 或组合引擎关闭时该属性不可用，不影响自绘内容。
        }
    }
}
