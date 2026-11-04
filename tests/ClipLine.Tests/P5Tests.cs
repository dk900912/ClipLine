using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>P5：16 项菜单 / 11 语言 / 主题与无障碍 / 打包清单。</summary>
public sealed class P5Tests : ITestSuite
{
    private static TrayMenuContext Context(
        int liveCount = 0,
        bool canPutBack = false,
        bool hasHeld = false,
        int keep = 0,
        int folderCount = 0,
        bool revealed = false) => new(
        liveCount, canPutBack, hasHeld, false, false,
        true, true, false, keep, CardSize.Medium,
        true, false,
        Enumerable.Range(1, folderCount).Select(i => $@"C:\d{i}").ToArray(),
        "Ctrl+Alt+T", revealed);

    public void Register(TestRunner t)
    {
        t.Case("T-05a", "托盘菜单恰好 16 项且顺序固定", () =>
        {
            var items = TrayMenuModel.Build(Context());
            Check.Equal(16, items.Count, "主项数量");
            Check.Equal(TrayMenuModel.MainItemCount, items.Count, "常量一致");
            Check.Equal(1, items[0].Order, "第一项显示/隐藏");
            Check.Equal(16, items[^1].Order, "最后一项退出");
            Check.Equal(TrayMenuAction.Quit, items[^1].Action, "退出动作");
            Check.True(items[12].SeparatorBefore, "声音前有分隔符");
            Check.True(items[15].SeparatorBefore, "退出前有分隔符");
        });

        t.Case("T-05b", "空绳时取下 / 挂回 / 全部放回均禁用", () =>
        {
            var items = TrayMenuModel.Build(Context());
            Check.False(items[1].Enabled, "全部取下禁用");
            Check.False(items[2].Enabled, "挂回去禁用");
            Check.False(items[3].Enabled, "全部放回禁用");
            Check.True(items[11].Enabled, "打开截图文件夹始终可用");
        });

        t.Case("T-05c", "有内容时条件项按状态启用", () =>
        {
            var items = TrayMenuModel.Build(Context(liveCount: 3, canPutBack: true, hasHeld: true));
            Check.True(items[1].Enabled, "全部取下可用");
            Check.True(items[2].Enabled, "挂回去可用");
            Check.True(items[3].Enabled, "全部放回可用");
            Check.Equal("Ctrl+Z", items[2].Shortcut, "挂回去显示 Windows 快捷键");
        });

        t.Case("T-01b", "第一项动态标题并显示当前快捷键", () =>
        {
            var hidden = TrayMenuModel.Build(Context())[0];
            Check.Equal("Show Line", hidden.Key, "收起时显示『显示』");
            var shown = TrayMenuModel.Build(Context(revealed: true))[0];
            Check.Equal("Hide Line", shown.Key, "展开时显示『隐藏』");
            Check.Equal("Ctrl+Alt+T", shown.Shortcut, "显示当前快捷键");
        });

        t.Case("T-06", "监视文件夹上限 3 时禁止添加且可停止监视", () =>
        {
            var three = TrayMenuModel.Build(Context(folderCount: 3));
            var children = three[7].Children!;
            var add = children.First(c => c.Action == TrayMenuAction.AddFolder);
            Check.False(add.Enabled, "已到 3 个时禁用添加");
            var stop = children.First(c => c.Action == TrayMenuAction.StopWatching);
            Check.True(stop.Enabled, "有文件夹时可停止监视");
            Check.Equal(3, stop.Children!.Count, "停止监视列出 3 个");

            var one = TrayMenuModel.Build(Context(folderCount: 1));
            Check.True(one[7].Children!.First(c => c.Action == TrayMenuAction.AddFolder).Enabled, "未到上限可添加");
        });

        t.Case("T-05d", "复选态与四档保留 / 三档大小", () =>
        {
            var c = new TrayMenuContext(
                2, false, false, true, true, true, false, true, 50, CardSize.Large,
                false, true, Array.Empty<string>(), "Ctrl+Alt+T", false);
            var items = TrayMenuModel.Build(c);
            Check.True(items[4].Checked, "接管截图复选");
            Check.True(items[5].Checked, "复制图片复选");
            Check.False(items[12].Checked, "声音关");
            Check.True(items[13].Checked, "登录时打开复选");

            var reveal = items[6].Children!;
            Check.True(reveal[0].Checked, "指针停在顶部栏 = 开");
            Check.False(reveal[1].Checked, "有新内容挂上 = 关");
            Check.True(reveal[2].Checked, "全屏上显示 = 开");

            var keep = items[8].Children!;
            Check.Equal(4, keep.Count, "四档保留");
            Check.True(keep[2].Checked, "50 张选中");
            var sizes = items[9].Children!;
            Check.True(sizes[2].Checked, "大档选中");

            var shortcut = items[10].Children!;
            Check.False(shortcut[0].Enabled, "当前快捷键为禁用项");
            Check.True(shortcut[1].Enabled, "更改快捷键可用");
        });

        t.Case("P-03a", "11 种语言且所有 key 齐全", () =>
        {
            Check.Equal(11, Localization.Languages.Length, "语言数");
            Check.True(Localization.ValidateAll(), "等价于 check-strings.swift 的校验");
            foreach (string language in Localization.Languages)
                Check.Equal(0, Localization.MissingKeys(language).Count, language);
            Check.True(Localization.IsSupported("zh-Hans"), "默认语言");
            Check.False(Localization.IsSupported("xx-YY"), "未知语言");
        });

        t.Case("P-03b", "简体中文文案与英文 key", () =>
        {
            Check.Equal("截个图，它就会挂在这里", Localization.Get("Screenshot hangs here", "zh-Hans"), "空态提示");
            Check.Equal("已复制", Localization.Get("Copied", "zh-Hans"), "已复制徽标");
            Check.Equal("退出 ClipLine", Localization.Get("Quit ClipLine", "zh-Hans"), "退出菜单");
            Check.Equal("Copy", Localization.Get("Copy", "en"), "英文源语言");
            Check.Equal("Copy", Localization.Get("Copy", "ja"), "未校对语言回落英文");
        });

        t.Case("K-06b", "菜单快捷键统一显示 Windows 符号", () =>
        {
            Check.Equal("Ctrl+Z", Localization.DisplayShortcut("⌘Z"), "⌘");
            Check.Equal("Ctrl+Alt+T", Localization.DisplayShortcut("⌃⌥T"), "默认快捷键");
            Check.Equal("Alt+Tab", Localization.DisplayShortcut("⌥Tab"), "⌥");
        });

        t.Case("P-02", "三套主题令牌与无障碍标签", () =>
        {
            Check.Equal("#F9FAFBCC", ThemeTokens.For(ThemeMode.Light).CardGlass, "浅色玻璃框");
            Check.Equal("#2B2F3ACC", ThemeTokens.For(ThemeMode.Dark).CardGlass, "深色玻璃框");
            Check.Equal("#000000", ThemeTokens.For(ThemeMode.HighContrast).MenuBackground, "高对比度");
            Check.Equal(4, ThemeTokens.AccessibilityLabels.Length, "四个 UIA 标签");
            Check.True(ThemeTokens.ShouldSkipAnimation(true), "减少动效跳过动画");
            Check.False(ThemeTokens.ShouldSkipAnimation(false), "正常播放动画");
        });

        t.Case("T-05e", "门面：每次打开菜单都重建并即时刷新托盘", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new NewItem(Fixture.P(1)));
            var r = f.Send(new TrayMenuOpening());
            Check.True(r.Has(CommandKind.ShowTrayMenu), "发出菜单");
            Check.Contains("Quit ClipLine", r.Arg(CommandKind.ShowTrayMenu), "含退出项");
            Check.Contains("2:TakeDownAll", r.Arg(CommandKind.ShowTrayMenu), "含全部取下且已重建");
            Check.True(r.Has(CommandKind.SetTrayTooltip), "T-04 即时刷新");
        });

        t.Case("T-05f", "门面：菜单动作复用已测输入，不重复实现", () =>
        {
            var f = new Fixture();
            f.Start();
            var keep = f.Send(new TrayMenuItemInvoked(TrayMenuAction.Keep25));
            Check.Equal(25, f.State.MaxItems, "绳上保留 25 张");
            Check.True(keep.Has(CommandKind.PersistState), "持久化");
            var size = f.Send(new TrayMenuItemInvoked(TrayMenuAction.SizeLarge));
            Check.Equal(CardSize.Large, f.State.Size, "大档");
            Check.True(size.Has(CommandKind.ReloadThumbnail), "缩略图全部重生成");
            var quit = f.Send(new TrayMenuItemInvoked(TrayMenuAction.Quit));
            Check.True(quit.Has(CommandKind.RestoreSettings), "退出还原设置");
        });

        t.Case("P-02b", "门面：主题与减少动效命令", () =>
        {
            var f = new Fixture();
            f.Start();
            var r = f.Send(new ThemeChanged(ThemeMode.Dark, true));
            Check.Contains("Dark", r.Arg(CommandKind.SetTheme), "深色主题");
            Check.Equal("1", r.Arg(CommandKind.SetReduceMotion), "减少动效");
        });

        t.Case("P-03c", "门面：语言切换与本地化查询", () =>
        {
            var f = new Fixture();
            f.Start();
            var ok = f.Send(new SetLanguage("ja"));
            Check.Equal("ja", f.State.Language, "切换成功");
            Check.True(ok.Has(CommandKind.SetLanguageText), "通知 UI 刷新");
            var bad = f.Send(new SetLanguage("klingon"));
            Check.True(bad.Has(CommandKind.Beep), "未知语言蜂鸣");
            Check.Equal("ja", f.State.Language, "不改值");
            f.Send(new SetLanguage("zh-Hans"));
            var text = f.Send(new LocalizationQuery("Copied"));
            Check.Equal("Copied=已复制", text.Arg(CommandKind.ApplyLocalizedText), "中文查询");
        });

        t.Case("L-02", "面向用户的文案只出现 ClipLine 品牌", () =>
        {
            var all = Localization.Languages.SelectMany(l => Localization.Keys.Select(k => Localization.Get(k, l)));
            Check.False(all.Any(s => s.Contains("Tendedero", StringComparison.OrdinalIgnoreCase)), "不含原项目名称");
            Check.True(Localization.Get("Quit ClipLine", "zh-Hans").Contains("ClipLine"), "品牌名保留");
        });
    }
}
