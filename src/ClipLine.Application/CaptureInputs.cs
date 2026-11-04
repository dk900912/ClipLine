using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>目录监听报告新文件；IsComplete 由平台层读文件尾字节后给出（F-08）。</summary>
public sealed record FileCreated(
    string Path,
    CaptureSource Source,
    long CreatedAtMs,
    bool HasScreenCaptureMark = false,
    bool IsComplete = true,
    long Size = 0) : AppInput;

/// <summary>写入中的文件大小变化，用于「0.5 秒内大小不变」判定。</summary>
public sealed record FileSizeChanged(string Path, long Size) : AppInput;

/// <summary>平台层复查后确认文件写完（true）或仍未写完（false）。</summary>
public sealed record FileIntegrityResolved(string Path, bool Complete) : AppInput;

public sealed record ClipboardChanged(ClipboardSnapshot Snapshot, DateTime Timestamp) : AppInput;

/// <summary>N-04：Windows 无 kMDItemScreenCaptureGlobalRect，由剪贴板图像与主屏坐标推算选区。</summary>
public sealed record CaptureRegion(int X, int Y, int Width, int Height) : AppInput;
