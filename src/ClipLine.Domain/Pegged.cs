using System.Text.Json.Serialization;

namespace ClipLine.Domain;

/// <summary>卡片尺寸档位。对应 V-11。</summary>
public enum CardSize
{
    Small,
    Medium,
    Large,
}

/// <summary>左上角按钮语义。对应 V-16 / G-05。</summary>
public enum CornerAction
{
    Trash,
    Hide,
    Unpin,
}

/// <summary>条目来源。影响提示音（V-29）、角标（D-03）与文件语义（D-07）。</summary>
public enum ItemOrigin
{
    Screenshot,
    Clipboard,
    WatchedFolder,
    ShellExtension,
    Restore,
}

/// <summary>
/// 绳上一张照片。字段与 Swift 的 Pegged 一一对应（D-01）。
/// 不持有缩略图位图：每张卡片内存里只留一张缩略图由渲染层缓存（D-10）。
/// </summary>
public sealed class Pegged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = string.Empty;
    public double Tilt { get; set; }
    public bool Falling { get; set; }
    public bool Flying { get; set; }
    public bool Kept { get; set; }
    public bool Held { get; set; }
    public bool ByHand { get; set; }
    public bool IsRecording { get; set; }
    public ItemOrigin Origin { get; set; } = ItemOrigin.Screenshot;
    public bool InOwnDirectory { get; set; } = true;
    public long CreatedAtMs { get; set; }

    /// <summary>普通照片：不是掉落中、不是固定、不是拿在手上、不是手动挂上（V-33）。</summary>
    [JsonIgnore]
    public bool IsLoose => !Falling && !Kept && !Held && !ByHand;

    [JsonIgnore]
    public bool IsLive => !Falling;

    public Pegged Copy() => (Pegged)MemberwiseClone();
}
