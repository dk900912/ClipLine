namespace ClipLine.Domain;

public enum ThemeMode
{
    Light,
    Dark,
    HighContrast,
}

/// <summary>docs/05-ui-design.md 的设计令牌；绳保持中性灰白，品牌渐变只用于 Logo 与强调。</summary>
public sealed record ThemePalette(
    string Rope,
    string RopeHighlight,
    string RopeShadow,
    string CardGlass,
    string Ink,
    string MenuBackground,
    string MenuBorder,
    string BrandStart,
    string BrandEnd,
    string AccentWarm);

public static class ThemeTokens
{
    public static readonly ThemePalette Light = new(
        "#FFFFFF", "#FFFFFF", "#00000038", "#F9FAFBCC", "#252A34",
        "#F3F3F3F2", "#E5E5E5", "#4F5BD5", "#2AA9E0", "#EF8A24");

    public static readonly ThemePalette Dark = new(
        "#E8EAF0", "#FFFFFF", "#00000066", "#2B2F3ACC", "#F2F4F8",
        "#2B2B2BF2", "#454545", "#6C63F4", "#2AA9E0", "#F7BB5C");

    public static readonly ThemePalette HighContrast = new(
        "#FFFFFF", "#FFFFFF", "#000000", "#000000", "#FFFFFF",
        "#000000", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFF00");

    public static ThemePalette For(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => Dark,
        ThemeMode.HighContrast => HighContrast,
        _ => Light,
    };

    /// <summary>P-02：无障碍标签（托盘 / 卡片 / 角标 / 放回按钮）。</summary>
    public static readonly string[] AccessibilityLabels =
    {
        "ClipLine tray icon",
        "line item image",
        "corner button",
        "Back on the Line",
    };

    /// <summary>V-30：减少动态效果时跳过飞入与下落。</summary>
    public static bool ShouldSkipAnimation(bool reduceMotion) => reduceMotion;
}
