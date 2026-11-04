namespace ClipLine.Application;

/// <summary>
/// P-03：11 种语言，源码以英文原文作 key（对应原项目的 L("...") 与 check-strings.swift）。
/// en 为源语言，zh-Hans 为默认；其余 9 种目前回落到英文，
/// key 集合全部齐全并由 ValidateAll 校验，**译文待母语校对**（见 docs/07）。
/// </summary>
public static class Localization
{
    public static readonly string[] Languages =
    {
        "en", "es", "gl", "fr", "pt-BR", "ja", "ko", "zh-Hans", "tr", "az", "hi",
    };

    public static readonly string[] Keys =
    {
        "Copy", "Open", "Quick Look", "Hold", "Markup", "Reveal in Explorer",
        "Pin", "Unpin", "Store to Desktop", "Take Down", "Discard",
        "Move to Recycle Bin", "Take Down Left", "Take Down Right",
        "Show Line", "Hide Line", "Take Down All", "Put Back", "Hang Everything Back",
        "Take Over Screenshots", "Hang Copied Images", "Lower the Line",
        "Watch Folders", "Keep on Line", "Size", "Shortcut", "Open Screenshots Folder",
        "Sound", "Launch at Login", "Check for Updates…", "Quit ClipLine",
        "Empty Line", "N Items on Line", "Copied", "Screenshot hangs here",
        "Pause and it will hang here", "Back on the Line",
    };

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        ["Copy"] = "复制",
        ["Open"] = "打开",
        ["Quick Look"] = "快速查看",
        ["Hold"] = "拿在手上",
        ["Markup"] = "标记",
        ["Reveal in Explorer"] = "在文件资源管理器中显示",
        ["Pin"] = "固定",
        ["Unpin"] = "取消固定",
        ["Store to Desktop"] = "存储到桌面",
        ["Take Down"] = "取下",
        ["Discard"] = "丢弃",
        ["Move to Recycle Bin"] = "移到回收站",
        ["Take Down Left"] = "取下左侧全部",
        ["Take Down Right"] = "取下右侧全部",
        ["Show Line"] = "显示晾衣绳",
        ["Hide Line"] = "隐藏晾衣绳",
        ["Take Down All"] = "全部取下",
        ["Put Back"] = "挂回去",
        ["Hang Everything Back"] = "全部放回绳上",
        ["Take Over Screenshots"] = "接管截图",
        ["Hang Copied Images"] = "挂上复制的图片",
        ["Lower the Line"] = "放下晾衣绳",
        ["Watch Folders"] = "同时监视文件夹",
        ["Keep on Line"] = "绳上保留",
        ["Size"] = "大小",
        ["Shortcut"] = "快捷键",
        ["Open Screenshots Folder"] = "打开截图文件夹",
        ["Sound"] = "声音",
        ["Launch at Login"] = "登录时打开",
        ["Check for Updates…"] = "检查更新…",
        ["Quit ClipLine"] = "退出 ClipLine",
        ["Empty Line"] = "晾衣绳上是空的",
        ["N Items on Line"] = "晾衣绳上有 N 张",
        ["Copied"] = "已复制",
        ["Screenshot hangs here"] = "截个图，它就会挂在这里",
        ["Pause and it will hang here"] = "截图即刻挂上，不再经过桌面",
        ["Back on the Line"] = "放回绳上",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> All = Build();

    private static Dictionary<string, Dictionary<string, string>> Build()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string language in Languages)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in Keys) table[key] = key; // 英文原文作 key，同时作为 fallback 值
            result[language] = table;
        }
        foreach (var pair in Chinese) result["zh-Hans"][pair.Key] = pair.Value;
        return result;
    }

    public static bool IsSupported(string language) =>
        Languages.Any(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));

    public static string Get(string key, string language)
    {
        if (!All.TryGetValue(language, out var table)) table = All["zh-Hans"];
        return table.TryGetValue(key, out string? value) ? value : key;
    }

    public static IReadOnlyList<string> MissingKeys(string language)
    {
        if (!All.TryGetValue(language, out var table)) return Keys;
        return Keys.Where(k => !table.ContainsKey(k)).ToArray();
    }

    /// <summary>等价于原项目 check-strings.swift 的校验。</summary>
    public static bool ValidateAll() => Languages.All(l => MissingKeys(l).Count == 0);

    /// <summary>N-10：菜单中的 macOS 符号统一显示成 Windows 符号。</summary>
    public static string DisplayShortcut(string shortcut) => shortcut
        .Replace("⌘", "Ctrl+", StringComparison.Ordinal)
        .Replace("⌥", "Alt+", StringComparison.Ordinal)
        .Replace("⌃", "Ctrl+", StringComparison.Ordinal)
        .TrimEnd('+');
}
