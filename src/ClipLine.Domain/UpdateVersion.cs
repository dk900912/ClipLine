namespace ClipLine.Domain;

/// <summary>
/// 版本解析与比较的纯函数。供 GitHub Release 更新检查使用；不含任何 I/O，
/// 因此可以在唯一接缝的测试里直接用假版本串断言，无需真实网络。
/// 支持 "1.0.0.0" / "v1.2.3" / "1.2.3-beta.2+build" 等形态。
/// </summary>
public static class UpdateVersion
{
    /// <summary>解析成固定的 4 段（不足补 0）；预发布 / 构建元数据忽略。</summary>
    public static bool TryParse(string? raw, out int[] parts)
    {
        parts = new int[4];
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];

        string[] fields = s.Split('.');
        if (fields.Length is < 2 or > 4) return false;
        for (int i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i], out int value) || value < 0) return false;
            parts[i] = value;
        }
        return true;
    }

    /// <summary>负数=左旧，0=相同，正数=左新；无法解析时退化为序号比较。</summary>
    public static int Compare(string? a, string? b)
    {
        bool okA = TryParse(a, out int[] va);
        bool okB = TryParse(b, out int[] vb);
        if (!okA || !okB) return string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);
        for (int i = 0; i < 4; i++)
        {
            int c = va[i].CompareTo(vb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;
}
