using System.Text.Json;

namespace ClipLine.Domain;

/// <summary>D-09：只持久化路径顺序与固定 / 手动挂上集合，不存位图。</summary>
public sealed class PersistedState
{
    public List<string> Pegged { get; set; } = new();
    public List<string> Kept { get; set; } = new();
    public List<string> ByHand { get; set; } = new();
    public Settings Settings { get; set; } = new();
}

public sealed record RestorePlan(List<Pegged> Items, Settings Settings, int ScreenWidthDip);

/// <summary>
/// 纯函数式序列化：真实落盘由 IFileStore 完成（平台层原子写 + 备份）。
/// 这里不碰文件系统，便于用假存储完整测试启动恢复路径。
/// </summary>
public static class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(PersistedState state) => JsonSerializer.Serialize(state, Options);

    public static PersistedState Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PersistedState();
        try
        {
            return JsonSerializer.Deserialize<PersistedState>(json, Options) ?? new PersistedState();
        }
        catch (JsonException)
        {
            // 状态文件损坏时不能阻止启动；回落到全新状态，由上层决定是否重建。
            return new PersistedState();
        }
    }

    public static PersistedState Capture(Line line, Settings settings) => new()
    {
        Pegged = line.Items.Where(i => i.IsLive).Select(i => i.Path).ToList(),
        Kept = line.Items.Where(i => i.IsLive && i.Kept).Select(i => i.Path).ToList(),
        ByHand = line.Items.Where(i => i.IsLive && i.ByHand).Select(i => i.Path).ToList(),
        Settings = settings.Copy(),
    };

    /// <summary>
    /// isUsable = 文件仍存在且能生成缩略图；不通过的文件直接跳过（D-09）。
    /// 恢复阶段不淘汰，交给 Line.RestoreFrom 在屏宽确定后统一重算。
    /// </summary>
    public static RestorePlan Restore(string json, Func<string, bool> isUsable, int screenWidthDip)
    {
        var state = Deserialize(json);
        var kept = state.Kept.Select(Line.NormalizePath).ToHashSet();
        var byHand = state.ByHand.Select(Line.NormalizePath).ToHashSet();
        var seen = new HashSet<string>();
        var items = new List<Pegged>();

        foreach (var path in state.Pegged)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string normalized = Line.NormalizePath(path);
            if (!seen.Add(normalized) || !isUsable(path)) continue;
            bool isByHand = byHand.Contains(normalized);
            items.Add(new Pegged
            {
                Path = path,
                Kept = kept.Contains(normalized) || isByHand,
                ByHand = isByHand,
                Origin = ItemOrigin.Restore,
            });
        }

        return new RestorePlan(items, state.Settings, screenWidthDip);
    }

    /// <summary>A-09：开启接管前先备份原设置；关闭/退出/崩溃恢复时写回原值。</summary>
    public static bool IsBackupOfOwnSettings(string? backup) =>
        !string.IsNullOrWhiteSpace(backup) && backup.Contains("ClipLine", StringComparison.OrdinalIgnoreCase);
}
