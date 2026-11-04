namespace ClipLine.Domain;

/// <summary>
/// 与 docs/01 §12 的 UserDefaults 键一一对应。所有键都存到
/// %LOCALAPPDATA%\ClipLine\settings.json（真实路径由平台层提供）。
/// 注意 macOS 侧几个「反向保存」的键，这里一律转正为肯定语义，避免双重否定。
/// </summary>
public sealed class Settings
{
    /// <summary>keepOnLine：0 = 放得下的数量；允许 25 / 50 / 100。</summary>
    public int KeepOnLine { get; set; }

    /// <summary>lineSize，默认 medium。</summary>
    public CardSize LineSize { get; set; } = CardSize.Medium;

    /// <summary>soundOff 取反，默认有声。</summary>
    public bool SoundOn { get; set; } = true;

    /// <summary>clipboardEnabled：默认关闭，opt-in（C-01）。</summary>
    public bool ClipboardEnabled { get; set; }

    /// <summary>inboxEnabled：接管截图（Windows 上为「监视默认截图目录」）。</summary>
    public bool InboxEnabled { get; set; }

    /// <summary>inboxOffered：首次询问只弹一次。</summary>
    public bool InboxOffered { get; set; }

    /// <summary>menuBarRevealOff 取反，默认开（S-10）。</summary>
    public bool RevealOnPointer { get; set; } = true;

    /// <summary>quietHang 取反，默认开（S-10）。</summary>
    public bool PeekOnNewContent { get; set; } = true;

    /// <summary>showOverFullScreen，默认关（S-10）。</summary>
    public bool ShowOverFullScreen { get; set; }

    /// <summary>extraFolders，最多 3 个（F-10 / T-06）。</summary>
    public List<string> ExtraFolders { get; set; } = new();

    /// <summary>shortcut，默认 ⌃⌥T → Ctrl+Alt+T（K-01 / K-06）。</summary>
    public string Shortcut { get; set; } = "Ctrl+Alt+T";

    /// <summary>welcomed：首次运行欢迎。</summary>
    public bool Welcomed { get; set; }

    /// <summary>launchAtLogin（A-03）。</summary>
    public bool LaunchAtLogin { get; set; }

    /// <summary>界面语言，默认简体中文（P-03）。</summary>
    public string Language { get; set; } = "zh-Hans";

    public const int MaxExtraFolders = 3;

    public static readonly int[] AllowedKeepOnLine = { 0, 25, 50, 100 };

    public static bool IsValidKeep(int value) => Array.IndexOf(AllowedKeepOnLine, value) >= 0;

    public Settings Copy() => (Settings)MemberwiseClone();
}
