using System;
using ClipLine.Shell;
using Microsoft.UI.Dispatching;

namespace ClipLine.AppHost;

/// <summary>
/// 应用宿主：OnLaunched 后组合 ShellAppHost（门面 + 绳窗口 + 托盘），
/// 用 33ms DispatcherQueueTimer 把真实指针送进门面 —— 对应 P0 验证过的 30 fps 帧驱动。
/// v1 挂上真实输入桥（剪贴板 / 目录 / 托盘菜单 / 卡片鼠标 / 快捷键），
/// 界面本体仍是自管分层 HWND + GDI DIB，预览/标注等交给系统默认应用。
/// </summary>
public sealed class App : Microsoft.UI.Xaml.Application
{
    private readonly bool _smoke;
    private readonly Action<int> _setExitCode;
    private ShellAppHost? _host;
    private DispatcherQueueTimer? _frameTimer;

    public App(bool smoke, Action<int> setExitCode)
    {
        _smoke = smoke;
        _setExitCode = setExitCode;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (_smoke)
        {
            // 冒烟：在 WinUI 启动路径内同步跑完（无需消息泵），退出码即结论。
            int code = ShellSmoke.Run();
            _setExitCode(code);
            Environment.Exit(code);
            return;
        }

        _host = new ShellAppHost();
        _host.Start();
        _host.StartRealTime();
        _host.QuitRequested += Shutdown;

        _frameTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _frameTimer.Interval = TimeSpan.FromMilliseconds(33); // ≈ 30 fps，与 P0 切片一致
        _frameTimer.Tick += (_, _) =>
        {
            (int x, int y) = CursorReader.Position();
            _host.Tick(x, y);
        };
        _frameTimer.Start();
    }

    private void Shutdown()
    {
        _frameTimer?.Stop();
        _host?.Dispose();
        Microsoft.UI.Xaml.Application.Current?.Exit();
        Environment.Exit(0);
    }
}
