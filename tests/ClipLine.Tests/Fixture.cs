using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>统一夹具：时钟、剪贴板、目录监听、拖放、合成器全部 fake，只有一个应用门面接缝。</summary>
public sealed class Fixture
{
    public const string BaselineJson = """
        {"Pegged":[],"Kept":[],"ByHand":[],
         "Settings":{"Welcomed":true,"InboxOffered":true}}
        """;

    public FakeClock Clock { get; } = new();
    public FakeEnvironment Env { get; }
    public RecordingCommandSink Sink { get; } = new();
    public AppFacade App { get; }
    public long NowMs { get; private set; }

    public Fixture(int widthDip = 1280, Action<FakeEnvironment>? configure = null, string? persistedJson = null)
    {
        Env = new FakeEnvironment { ScreenWidthDip = widthDip };
        configure?.Invoke(Env);
        App = new AppFacade(new AppDependencies
        {
            Clock = Clock,
            Environment = Env,
            Sink = Sink,
            PersistedJson = persistedJson,
        });
    }

    public AppResult Send(AppInput input) => App.Handle(input);

    public AppResult Advance(long deltaMs)
    {
        NowMs += deltaMs;
        Clock.NowMs = NowMs;
        return App.AdvanceTo(NowMs);
    }

    public StateSnapshot State => App.Current;

    public AppResult Start(string? json = null, bool fresh = false) =>
        Send(new ApplicationStarted(json ?? BaselineJson, fresh));

    public static string P(int n) => $@"C:\shots\s{n}.png";

    public static bool Has(AppResult result, CommandKind kind) => result.Commands.Any(c => c.Kind == kind);

    public static int Count(AppResult result, CommandKind kind) => result.Commands.Count(c => c.Kind == kind);

    public static string? Arg(AppResult result, CommandKind kind) =>
        result.Commands.FirstOrDefault(c => c.Kind == kind)?.Argument;

    public static AppCommand? First(AppResult result, CommandKind kind) =>
        result.Commands.FirstOrDefault(c => c.Kind == kind);
}

public static class ResultExtensions
{
    public static bool Has(this AppResult result, CommandKind kind) => Fixture.Has(result, kind);
    public static int Count(this AppResult result, CommandKind kind) => Fixture.Count(result, kind);
    public static string? Arg(this AppResult result, CommandKind kind) => Fixture.Arg(result, kind);
    public static AppCommand? First(this AppResult result, CommandKind kind) => Fixture.First(result, kind);
}
