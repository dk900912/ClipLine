using System.Reflection;
using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>P6：全表追溯、隐私 / 性能 / 交付完整性放行检查。</summary>
public sealed class P6Tests : ITestSuite
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "docs", "01-feature-alignment.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录");
    }

    public void Register(TestRunner t)
    {
        t.Case("P6-01", "功能全表追溯矩阵已生成且编号可回溯", () =>
        {
            string path = System.IO.Path.Combine(Root(), "docs/verification/traceability.md");
            Check.True(File.Exists(path), "矩阵文件存在");
            string text = File.ReadAllText(path);
            Check.Contains("验收编号追溯矩阵", text, "标题");
            Check.Contains("`A-01`", text, "含生命周期编号");
            Check.Contains("`V-31`", text, "含视觉几何编号");
            Check.Contains("`T-05`", text, "含托盘菜单编号");
            Check.Contains("`R-01`", text, "含 v1.8.1 回归编号");
            var match = System.Text.RegularExpressions.Regex.Match(text, @"自动化覆盖：\*\*(\d+) / (\d+)");
            Check.True(match.Success, "含覆盖率统计");
            int covered = int.Parse(match.Groups[1].Value);
            int total = int.Parse(match.Groups[2].Value);
            Check.True(total >= 90, $"功能清单编号不少于 90 项（实际 {total}）");
            Check.True(covered * 2 >= total, $"自动化覆盖不低于 50%（实际 {covered}/{total}）");
        });

        t.Case("L-03", "源码零联网 API / 零 URL 常量", () =>
        {
            string[] forbidden =
            {
                "HttpClient", "WebRequest", "WebClient", "Socket(",
                "TelemetryClient", "ApplicationInsights", "System.Net.Http",
                "System.Net.Sockets", "http://", "https://",
            };
            // 排除 SDK 自动生成的 obj/ 与 bin/（ImplicitUsings 会生成 System.Net.Http 的 global using）。
            var files = Directory.GetFiles(System.IO.Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var hits = new List<string>();
            foreach (string file in files)
            {
                string content = File.ReadAllText(file);
                foreach (string word in forbidden)
                {
                    if (content.Contains(word, StringComparison.Ordinal))
                        hits.Add(System.IO.Path.GetFileName(file) + ": " + word);
                }
            }
            Check.True(files.Length >= 20, $"覆盖全部 C# 源码（实际 {files.Length} 个文件）");
            Check.Equal(0, hits.Count, "联网依赖数量：" + string.Join(", ", hits));
        });

        t.Case("P6-02", "唯一测试接缝未被后续阶段破坏", () =>
        {
            var facade = typeof(AppFacade);
            Check.True(facade.IsSealed, "门面为 sealed");
            Check.True(facade.IsPublic, "门面是唯一公开入口");
            var sinks = typeof(AppFacade).Assembly.GetTypes()
                .Count(x => typeof(ICommandSink).IsAssignableFrom(x) && !x.IsInterface);
            // 门面程序集里只有假命令接收器；真实实现位于 Platform 程序集。
            Check.Equal(1, sinks, "Application 层只有一个命令接收器实现");
        });

        t.Case("P6-03", "状态机时序常量与源码一致", () =>
        {
            Check.Equal(250, Layout.RevealDelayMs, "热区 0.25 s");
            Check.Equal(500, Layout.RetractDelayMs, "离开 0.5 s");
            Check.Equal(2500, Layout.PeekMs, "peek 2.5 s");
            Check.Equal(700, Layout.EmptyHideMs, "空绳 0.7 s");
            Check.Equal(300, Layout.DestroyDelayMs, "销毁 0.3 s");
            Check.Equal(800, Layout.FullScreenRecheckMs, "全屏复查 0.8 s");
            Check.Equal(600, Layout.GapCloseMs, "空隙闭合 0.6 s");
            Check.Equal(450, Layout.LongPressMs, "长按 0.45 s");
            Check.Equal(650, Layout.FlightMs, "飞入 0.65 s");
            Check.Equal(550, Layout.FallMs, "下落 0.55 s");
            Check.Equal(20, UndoSteps.MaxSteps, "撤销 20 步");
            Check.Equal(3, Settings.MaxExtraFolders, "监视文件夹 ≤3");
        });

        t.Case("P6-04", "设置默认值与 01 §12 一致", () =>
        {
            var s = new Settings();
            Check.False(s.ClipboardEnabled, "剪贴板默认关");
            Check.False(s.InboxEnabled, "接管截图默认关");
            Check.True(s.SoundOn, "声音默认开");
            Check.True(s.RevealOnPointer, "指针展开默认开");
            Check.True(s.PeekOnNewContent, "新内容展开默认开");
            Check.False(s.ShowOverFullScreen, "全屏显示默认关");
            Check.Equal(CardSize.Medium, s.LineSize, "中档默认");
            Check.Equal(0, s.KeepOnLine, "放得下的数量");
            Check.Equal("Ctrl+Alt+T", s.Shortcut, "默认快捷键");
            Check.Equal("zh-Hans", s.Language, "默认简体中文");
        });

        t.Case("P6-05", "本地化与菜单在放行前仍通过校验", () =>
        {
            Check.True(Localization.ValidateAll(), "11 语言 key 齐全");
            var context = new TrayMenuContext(
                0, false, false, false, false, true, true, false, 0,
                CardSize.Medium, true, false, Array.Empty<string>(), "Ctrl+Alt+T", false);
            Check.Equal(16, TrayMenuModel.Build(context).Count, "16 项菜单");
            Check.True(StateStore.Deserialize("{}").Settings.SoundOn, "损坏状态可回落");
        });

        t.Case("P6-06", "打包与验证工具齐备", () =>
        {
            string root = Root();
            foreach (string relative in new[]
            {
                "tools/verify.sh", "tools/check-privacy.sh", "tools/traceability.mjs",
                "packaging/AppxManifest.xml", "packaging/README.md", "packaging/icon-manifest.json",
                "docs/06-implementation-status.md", "docs/07-localization.md",
                "docs/05-ui-design.md", "assets/ui/prototype.html", "assets/logos/E-primary.svg",
            })
            {
                Check.True(File.Exists(System.IO.Path.Combine(root, relative)), relative);
            }
            string packaging = File.ReadAllText(System.IO.Path.Combine(root, "packaging/README.md"));
            Check.Contains("15 MB", packaging, "安装包预算已声明");
            Check.Contains("零出站", packaging, "零出站约束已声明");
        });

        t.Case("P6-07", "v1 范围边界被显式记录", () =>
        {
            string alignment = File.ReadAllText(System.IO.Path.Combine(Root(), "docs/01-feature-alignment.md"));
            Check.Contains("N-11", alignment, "多屏裁剪记录");
            Check.Contains("单屏范围内", alignment, "100% 对齐口径收紧");
            string status = File.ReadAllText(System.IO.Path.Combine(Root(), "docs/06-implementation-status.md"));
            Check.Contains("明确未覆盖", status, "实现状态文档列出边界");
        });

        t.Case("P6-08", "性能预算常量已固化", () =>
        {
            Check.Equal(210, Layout.PanelHeight(CardSize.Medium), "中档条带高度");
            Check.Equal(174, Layout.Spacing(CardSize.Medium), "卡片间距");
            Check.Equal(320, Layout.ThumbnailBase, "缩略图上限基准");
            Check.Equal(2, Layout.MaxConcurrentFlights, "最多 2 个并发飞行");
            Check.Equal(64L * 1024 * 1024, ClipboardFilter.MaxBytes, "剪贴板 64 MB");
            Check.Equal(80_000_000L, ClipboardFilter.MaxPixels, "8000 万像素");
            Check.Equal(600_000L, CaptureIntegrity.GiveUpAfterMs, "10 分钟放弃");
            Check.Equal(30, CaptureFlight.ArcLiftDip, "飞入弧线 30 pt");
        });
    }
}
