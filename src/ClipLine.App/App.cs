using System;
using ClipLine.Application;
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
    private readonly bool _menuAudit;
    private readonly bool _uiClick;
    private readonly Action<int> _setExitCode;
    private ShellAppHost? _host;
    private DispatcherQueueTimer? _frameTimer;

    public App(bool smoke, bool menuAudit, bool uiClick, Action<int> setExitCode)
    {
        _smoke = smoke;
        _menuAudit = menuAudit;
        _uiClick = uiClick;
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

        if (_menuAudit)
        {
            // 菜单全量审计：逐项触发托盘与卡片菜单，真实副作用只写隔离临时目录。
            int code = MenuAudit.Run();
            _setExitCode(code);
            Environment.Exit(code);
            return;
        }

        // --ui-click 会真实点击「固定」「取消固定」并挂夹具图，必须隔离状态文件，
        // 否则一次验证就会把测试图片写进用户真实的 state.json。
        if (_uiClick)
        {
            string isolated = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "clipline-uiclick", "state.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(isolated)!);
            // 每次验证都从干净的「首次运行」开始，避免上一轮夹具被状态恢复带回来。
            System.IO.File.WriteAllText(isolated, string.Empty);
            _host = new ShellAppHost(statePath: isolated);
        }
        else _host = new ShellAppHost();
        _host.Start();
        _host.StartRealTime();
        _host.QuitRequested += Shutdown;
        // 更新已交给一次性 helper：主进程退出后由 helper 替换 / 注册并重启。
        _host.RestartRequested += Shutdown;

        long startedAt = Environment.TickCount64;
        // 启动后只查一次，绝不轮询；离线静默失败。验证模式默认不联网，
        // 除非显式设置 CLIPLINE_UPDATE_API 指向本地模拟 Release 服务。
        bool autoUpdateDone = _uiClick && Environment.GetEnvironmentVariable("CLIPLINE_UPDATE_API") is null;
        _frameTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _frameTimer.Interval = TimeSpan.FromMilliseconds(33); // ≈ 30 fps，与 P0 切片一致
        _frameTimer.Tick += (_, _) =>
        {
            (int x, int y) = CursorReader.Position();
            _host.Tick(x, y);
            if (!autoUpdateDone && Environment.TickCount64 - startedAt >= 8_000)
            {
                autoUpdateDone = true;
                if (Environment.GetEnvironmentVariable("CLIPLINE_AUTO_UPDATE") != "0")
                    _host.Facade.Handle(new AutoUpdateTick());
            }
        };
        _frameTimer.Start();

        if (_uiClick)
        {
            // 真实桌面进程 + 正常帧驱动；验证器从独立线程注入系统级鼠标/键盘输入。
            UiClickTest.Start(_host, System.Threading.SynchronizationContext.Current!);
        }
    }

    private void Shutdown()
    {
        _frameTimer?.Stop();
        _host?.Dispose();
        Microsoft.UI.Xaml.Application.Current?.Exit();
        Environment.Exit(0);
    }
}
