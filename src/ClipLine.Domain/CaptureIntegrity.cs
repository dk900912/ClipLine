using System.Buffers.Binary;

namespace ClipLine.Domain;

public enum FileKind
{
    Png,
    Jpeg,
    Gif,
    WebP,
    Tiff,
    Heic,
    Mp4,
    Mov,
    Other,
}

public enum IntegrityVerdict
{
    Complete,
    Incomplete,
    TooOld,
}

/// <summary>
/// F-07 / F-08 / R-01 / R-02：v1.8.1 的核心修复——「先建空文件再慢慢写」的图片
/// 必须等文件完整才挂上绳，系统整体落盘的截图仍保持零延迟。
/// 判定只看字节，不碰文件系统，便于纯逻辑单测。
/// </summary>
public static class CaptureIntegrity
{
    public const long RetryIntervalMs = 500;
    public const long GiveUpAfterMs = 600_000;
    public const long StableWindowMs = 500;

    private static readonly byte[] PngIend =
    {
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    };

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".gif", ".webp",
    };

    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4",
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(Extension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(Extension(path));

    public static bool IsSupported(string path) => IsImage(path) || IsVideo(path);

    public static string Extension(string path)
    {
        try { return Path.GetExtension(path); }
        catch { return string.Empty; }
    }

    public static FileKind Classify(string path) => Extension(path).ToLowerInvariant() switch
    {
        ".png" => FileKind.Png,
        ".jpg" or ".jpeg" => FileKind.Jpeg,
        ".gif" => FileKind.Gif,
        ".webp" => FileKind.WebP,
        ".tif" or ".tiff" => FileKind.Tiff,
        ".heic" or ".heif" => FileKind.Heic,
        ".mp4" => FileKind.Mp4,
        ".mov" => FileKind.Mov,
        _ => FileKind.Other,
    };

    /// <summary>elapsedMs 超过 10 分钟直接放弃，不再重试（R-02）。</summary>
    public static IntegrityVerdict VerifyPath(string path, byte[] bytes, long? stableForMs, long elapsedMs)
    {
        if (elapsedMs >= GiveUpAfterMs) return IntegrityVerdict.TooOld;
        return Verify(Classify(path), bytes, stableForMs) ? IntegrityVerdict.Complete : IntegrityVerdict.Incomplete;
    }

    public static bool Verify(FileKind kind, byte[] bytes, long? stableForMs)
    {
        if (bytes.Length == 0) return false;
        return kind switch
        {
            FileKind.Png => EndsWith(bytes, PngIend),
            FileKind.Jpeg => HasJpegEoi(bytes),
            FileKind.Gif => bytes[^1] == 0x3B,
            FileKind.WebP => WebPLengthMatches(bytes),
            FileKind.Heic or FileKind.Mp4 or FileKind.Mov => BoxChainComplete(bytes),
            FileKind.Tiff or FileKind.Other => stableForMs.HasValue && stableForMs.Value >= StableWindowMs,
            _ => false,
        };
    }

    private static bool EndsWith(byte[] bytes, byte[] tail)
    {
        if (bytes.Length < tail.Length) return false;
        for (int i = 0; i < tail.Length; i++)
        {
            if (bytes[bytes.Length - tail.Length + i] != tail[i]) return false;
        }
        return true;
    }

    private static bool HasJpegEoi(byte[] bytes)
    {
        int from = Math.Max(0, bytes.Length - 8);
        for (int i = bytes.Length - 2; i >= from; i--)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD9) return true;
        }
        return false;
    }

    private static bool WebPLengthMatches(byte[] bytes)
    {
        if (bytes.Length < 12) return false;
        if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return false;
        if (bytes[8] != 'W' || bytes[9] != 'E' || bytes[10] != 'B' || bytes[11] != 'P') return false;
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        return declared + 8 == bytes.Length;
    }

    /// <summary>HEIC / MP4 / MOV 逐 box 校验链尾是否正好落在文件末尾。</summary>
    private static bool BoxChainComplete(byte[] bytes)
    {
        int pos = 0;
        while (pos + 8 <= bytes.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
            if (size == 1 && pos + 16 <= bytes.Length)
            {
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(pos + 8, 8));
                if (large < 16 || large > (ulong)bytes.Length - (ulong)pos) return false;
                pos += (int)large;
            }
            else if (size >= 8 && size <= bytes.Length - pos)
            {
                pos += (int)size;
            }
            else
            {
                return false;
            }
        }
        return pos == bytes.Length;
    }
}
