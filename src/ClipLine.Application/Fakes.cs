namespace ClipLine.Application;

/// <summary>假时钟：所有时序由 AdvanceTo 精确推进，不依赖真实时间。</summary>
public sealed class FakeClock : IClock
{
    public long NowMs { get; set; }
}

/// <summary>
/// 假环境：文件存在性、可解码性、全屏状态、主题与系统偏好全部可注入。
/// 真实实现由 Platform 层提供（读取主屏 rcWork / 单一系统缩放）。
/// </summary>
public sealed class FakeEnvironment : IEnvironment
{
    public int ScreenWidthDip { get; set; } = 1920;
    public double DisplayScale { get; set; } = 1.0;
    public bool IsFullScreenAppActive { get; set; }
    public bool ReduceMotion { get; set; }
    public bool IsDarkTheme { get; set; }
    public bool IsHighContrast { get; set; }
    public string OwnDirectory { get; set; } = @"C:\Users\test\AppData\Local\ClipLine\Screenshots";
    public string ScreenshotsFolder { get; set; } = @"C:\Users\test\Pictures\Screenshots";
    public string Version { get; set; } = "1.0.0";
    public double SystemDoubleClickIntervalMs { get; set; } = 500;

    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DecodableFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => ExistingFiles.Contains(path);
    public bool CanDecode(string path) => DecodableFiles.Contains(path) || ExistingFiles.Contains(path);
}

/// <summary>
/// 剪贴板 / 目录监听 / 拖放 / 合成器的统一 fake：门面只发命令，这里记录全部外部副作用。
/// 这就是定稿里「平台能力全部 fake」的落点。
/// </summary>
public sealed class RecordingCommandSink : ICommandSink
{
    public List<AppCommand> Commands { get; } = new();

    public void Execute(AppCommand command) => Commands.Add(command);

    public bool Has(CommandKind kind) => Commands.Any(c => c.Kind == kind);

    public int Count(CommandKind kind) => Commands.Count(c => c.Kind == kind);
}
