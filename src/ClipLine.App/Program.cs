using System;
using System.Linq;
using ClipLine.Shell;

namespace ClipLine.AppHost;

/// <summary>
/// 入口。--smoke 走端到端冒烟，--menu-audit 走两个场景的全量菜单审计；
/// 无参数进交互模式。WinUI 3 提供 DispatcherQueue 消息泵与 WindowsAppSDK 引导；
/// 绳窗口本体是自管分层 HWND（Shell 层）。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        bool menuAudit = args.Contains("--menu-audit", StringComparer.OrdinalIgnoreCase);

        // 必须早于任何 XAML / WindowsAppSDK 初始化，否则进程停留在 UNAWARE(96 DPI)。
        ShellDpi.EnsurePerMonitorV2();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        int exitCode = 0;
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(smoke, menuAudit, code => exitCode = code);
        });
        return exitCode;
    }
}
