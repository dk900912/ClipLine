using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>P1：领域模型 + 唯一应用门面接缝。编号对应 docs/01-feature-alignment.md。</summary>
public sealed class P1Tests : ITestSuite
{
    public void Register(TestRunner t)
    {
        t.Case("V-31", "可见数与容量 clamp(3,12)", () =>
        {
            Check.Equal(6, Layout.VisibleCount(1280, CardSize.Medium), "1280 DIP");
            Check.Equal(9, Layout.VisibleCount(1920, CardSize.Medium), "1920 DIP");
            Check.Equal(3, Layout.VisibleCount(320, CardSize.Medium), "下限");
            Check.Equal(12, Layout.VisibleCount(3840, CardSize.Medium), "上限");
        });

        t.Case("V-07", "抛物线下垂与 ropeY", () =>
        {
            Check.Near(23.04, Layout.Sag(1280), 0.001, "sag = 宽 × 0.018");
            Check.Near(30.0, Layout.Sag(3000), 0.001, "sag 上限 30");
            Check.Near(10.0, Layout.RopeY(0, 1280), 0.001, "左端");
            Check.Near(33.04, Layout.RopeY(640, 1280), 0.001, "中点");
            Check.Near(10.0, Layout.RopeY(1280, 1280), 0.001, "右端");
        });

        t.Case("V-11", "三档尺寸 / 间距 / 条带高度 / 缩略图", () =>
        {
            Check.Equal(120, Layout.CardWidth(CardSize.Small), "小档宽");
            Check.Equal(150, Layout.CardWidth(CardSize.Medium), "中档宽");
            Check.Equal(203, Layout.CardWidth(CardSize.Large), "大档宽（远离零舍入）");
            Check.Equal(83, Layout.PhotoMaxHeight(CardSize.Small), "小档高");
            Check.Equal(140, Layout.PhotoMaxHeight(CardSize.Large), "大档高");
            Check.Equal(174, Layout.Spacing(CardSize.Medium), "中档间距");
            Check.Equal(227, Layout.Spacing(CardSize.Large), "大档间距");
            Check.Equal(210, Layout.PanelHeight(CardSize.Medium), "中档条带 210");
            Check.Equal(246, Layout.PanelHeight(CardSize.Large), "大档条带");
            Check.Equal(256, Layout.ThumbnailLimit(CardSize.Small), "缩略图上限");
            Check.Equal(432, Layout.ThumbnailLimit(CardSize.Large), "缩略图上限");
        });

        t.Case("V-27", "飞入缩略图 clamp(400,1500)", () =>
        {
            Check.Equal(400, Layout.FlightThumbnailLimit(100, 1.0), "下限");
            Check.Equal(800, Layout.FlightThumbnailLimit(800, 1.0), "中间");
            Check.Equal(1500, Layout.FlightThumbnailLimit(5000, 1.0), "上限");
        });

        t.Case("V-28", "丢弃下落角度公式", () =>
        {
            double expected = 2.5 + (2.5 * 7 + 20) * Math.Pow(Math.E, 3);
            Check.Near(expected, Layout.FallTilt(2.5), 0.0001, "tilt + (tilt×7+20)·e³");
        });

        t.Case("V-33", "满绳一次只挤掉 1 张", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            for (int i = 1; i <= 6; i++) f.Send(new NewItem(Fixture.P(i)));
            Check.Equal(6, f.State.LiveCount, "1280 DIP 放得下 6 张");
            var r = f.Send(new NewItem(Fixture.P(7)));
            Check.Equal(6, f.State.LiveCount, "第 7 张后仍为 6");
            Check.True(r.Has(CommandKind.StartFall), "被挤掉的一张触发下落");
            Check.Equal(1, f.State.Items.Count(i => i.Falling), "只掉 1 张");
        });

        t.Case("V-33b", "固定照片永不自动掉落", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            for (int i = 1; i <= 8; i++) f.Send(new NewItem(Fixture.P(i), ByHand: true));
            Check.Equal(8, f.State.LiveCount, "8 张手动固定全部保留");
            Check.Equal(0, f.State.Items.Count(i => i.Falling), "无掉落");
        });

        t.Case("F-09", "同路径去重", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new NewItem(Fixture.P(1)));
            Check.Equal(1, f.State.LiveCount, "多个 watcher 报告同一文件只挂一次");
        });

        t.Case("D-02", "固定移到绳结左侧末尾", () =>
        {
            var line = new Line(1280);
            line.Hang(new Pegged { Path = "a.png" });
            line.Hang(new Pegged { Path = "b.png" });
            var c = new Pegged { Path = "c.png" };
            line.Hang(c);
            line.SetPinned(c.Id, true);
            Check.Equal(c.Id, line.Items[0].Id, "固定项排到最左");
            Check.Equal(1, line.KeptCount, "固定计数");
            line.SetPinned(c.Id, false);
            Check.False(line.Items[0].Kept, "取消固定后回到普通区最前");
        });

        t.Case("D-06a", "撤销栈最多 20 步", () =>
        {
            var undo = new UndoSteps();
            for (int i = 0; i < 25; i++) undo.Push(new TakenDownBatch());
            Check.Equal(20, undo.Count, "只保留 20 步");
            Check.True(undo.CanUndo, "仍可撤销");
        });

        t.Case("D-06b", "批量动作算一步且可完整放回", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            for (int i = 1; i <= 3; i++) f.Send(new NewItem(Fixture.P(i)));
            f.Send(new TakeDownAll());
            Check.Equal(1, f.State.UndoCount, "3 张一起取下只算 1 步");
            Check.Equal(0, f.State.LiveCount, "绳上清空");
            var r = f.Send(new PutBack());
            Check.Equal(3, f.State.LiveCount, "三张全部放回");
            Check.Equal(0, f.State.UndoCount, "撤销步已消耗");
            Check.True(r.Has(CommandKind.PlaySound), "放回有提示音");
            Check.True(r.Has(CommandKind.PersistState), "放回后持久化");
        });

        t.Case("D-09", "持久化只恢复仍存在且可解码的文件", () =>
        {
            var state = new PersistedState
            {
                Settings = new Settings { Welcomed = true, InboxOffered = true, KeepOnLine = 25 },
            };
            state.Pegged.Add(Fixture.P(1));
            state.Pegged.Add(Fixture.P(2));
            state.Pegged.Add(Fixture.P(3));
            state.Kept.Add(Fixture.P(1));
            state.ByHand.Add(Fixture.P(2));
            string json = StateStore.Serialize(state);

            var f = new Fixture(configure: env =>
            {
                env.ExistingFiles.Add(Fixture.P(1));
                env.ExistingFiles.Add(Fixture.P(2));
            });
            f.Start(json);
            Check.Equal(2, f.State.LiveCount, "缺失文件被跳过");
            Check.Equal(25, f.State.MaxItems, "恢复后按设置与屏宽重算容量");
            Check.Equal(2, f.State.Items.Count(i => i.Kept), "固定与手动挂上标志保留");
            Check.True(f.State.IsRevealed, "A-08：有保留照片时自动展开一次");
            Check.True(f.State.PeekActive, "A-08：伴随 2.5 秒 peek");
        });

        t.Case("D-09b", "状态文件损坏时安全回落", () =>
        {
            var restored = StateStore.Deserialize("{ not json");
            Check.Equal(0, restored.Pegged.Count, "不阻止启动");
            Check.True(restored.Settings.SoundOn, "回落到默认设置");
        });

        t.Case("S-03", "顶部热区停留 0.25 秒才展开", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new PointerMoved(640, 4));
            f.Advance(249);
            Check.False(f.State.IsRevealed, "249 ms 不展开");
            f.Advance(1);
            Check.True(f.State.IsRevealed, "250 ms 展开");
        });

        t.Case("S-03b", "空绳不被热区唤出", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new PointerMoved(640, 4));
            f.Advance(300);
            Check.False(f.State.IsRevealed, "绳上为空时热区无效");
        });

        t.Case("S-04", "离开 0.5 秒收起", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new PointerMoved(640, 4));
            f.Advance(250);
            Check.True(f.State.IsRevealed, "先展开");
            f.Send(new PointerMoved(640, 400));
            f.Advance(499);
            Check.True(f.State.IsRevealed, "499 ms 仍展开");
            f.Advance(1);
            Check.False(f.State.IsRevealed, "500 ms 收起");
        });

        t.Case("S-07", "新内容 peek 2.5 秒", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new NewItem(Fixture.P(1)));
            Check.True(f.State.IsRevealed, "新图自动展开");
            Check.True(f.State.PeekActive, "peek 生效");
            f.Advance(2499);
            Check.True(f.State.IsRevealed, "未到期不收起");
            f.Advance(1);
            Check.False(f.State.IsRevealed, "到期收起");
        });

        t.Case("S-08", "空绳 0.7 秒自动隐藏", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new TakeDownAll());
            f.Advance(699);
            Check.True(f.State.IsRevealed, "699 ms 仍展开");
            f.Advance(1);
            Check.False(f.State.IsRevealed, "700 ms 隐藏（优先于 peek）");
        });

        t.Case("V-05", "收起 0.3 秒后真正销毁窗口", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new ToggleLine());
            Check.True(f.State.IsPresent, "收起后窗口暂留");
            var before = f.Advance(299);
            Check.False(before.Has(CommandKind.DestroyWindow), "299 ms 未销毁");
            var after = f.Advance(1);
            Check.True(after.Has(CommandKind.DestroyWindow), "300 ms 销毁");
            Check.False(f.State.IsPresent, "不留不可见条带");
        });

        t.Case("S-05", "拖拽期间不收起，解除后收起", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new PointerMoved(640, 4));
            f.Advance(250);
            f.Send(new DragStateChanged(true));
            f.Send(new PointerMoved(640, 400));
            f.Advance(600);
            Check.True(f.State.IsRevealed, "拖拽中不收起");
            f.Send(new DragStateChanged(false));
            f.Send(new PointerMoved(640, 400));
            f.Advance(500);
            Check.False(f.State.IsRevealed, "解除后可正常收起");
        });

        t.Case("S-05b", "右键菜单打开期间线保持展开", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new PointerMoved(640, 4));
            f.Advance(250);
            f.Send(new MenuToggled(true));
            f.Send(new PointerMoved(640, 400));
            f.Advance(600);
            Check.True(f.State.IsRevealed, "菜单打开不收起");
            f.Send(new MenuToggled(false));
            f.Advance(500);
            Check.False(f.State.IsRevealed, "菜单关闭后收起");
        });

        t.Case("S-06", "点顶部栏立即收起并抑制到指针离开", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new PointerMoved(640, 4));
            f.Advance(250);
            f.Send(new TopBandClicked(640, 4));
            Check.False(f.State.IsRevealed, "立即收起");
            Check.True(f.State.MenuBarSuppressed, "抑制标志置位");
            f.Send(new PointerMoved(640, 4));
            f.Advance(300);
            Check.False(f.State.IsRevealed, "抑制期间热区不生效");
            f.Send(new PointerMoved(640, 400));
            Check.False(f.State.MenuBarSuppressed, "离开顶部栏后解除");
        });

        t.Case("S-09", "主动展开置 pinned / keepOpen / openedOnPurpose", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new ToggleLine());
            Check.True(f.State.IsRevealed, "主动展开");
            Check.True(f.State.Pinned, "pinned");
            Check.True(f.State.KeepOpen, "keepOpen");
            Check.True(f.State.OpenedOnPurpose, "openedOnPurpose");
            f.Send(new PointerMoved(640, 100));
            f.Send(new PointerMoved(640, 400));
            Check.False(f.State.OpenedOnPurpose, "指针来过并离开后解除");
        });

        t.Case("S-10", "三个「放下晾衣绳」开关默认值", () =>
        {
            var f = new Fixture();
            f.Start();
            Check.True(f.State.RevealOnPointer, "指针停在顶部栏 = 开");
            Check.True(f.State.PeekOnNewContent, "有新内容挂上时 = 开");
            Check.False(f.State.ShowOverFullScreen, "在全屏 App 上 = 关");
        });

        t.Case("S-11", "全屏 App 阻挡与 0.8 秒复查", () =>
        {
            var f = new Fixture(configure: e => e.IsFullScreenAppActive = true);
            f.Start();
            f.Send(new FullScreenChanged(true));
            Check.True(f.State.Blocked, "默认阻挡");
            f.Send(new SetShowOverFullScreen(true));
            Check.False(f.State.Blocked, "打开开关后不阻挡");
        });

        t.Case("T-01", "托盘三态与两行提示", () =>
        {
            var f = new Fixture();
            f.Start();
            Check.Equal(TrayState.Dimmed, f.State.Tray, "空绳变暗");
            var r = f.Send(new NewItem(Fixture.P(1)));
            Check.Equal(TrayState.Default, f.State.Tray, "有图默认态");
            Check.Contains("晾衣绳上有 1 张", r.Arg(CommandKind.SetTrayTooltip), "第二行提示");
            var r2 = f.Send(new SetClipboardEnabled(true));
            Check.Equal(TrayState.Monitoring, f.State.Tray, "监听开启填充态");
            Check.True(r2.Has(CommandKind.SetTrayIcon), "状态变化即时刷新图标");
            Check.Contains("正在挂上复制的图片", r2.Arg(CommandKind.SetTrayTooltip), "第一行提示");
            f.Send(new TakeDownAll());
            Check.Equal(TrayState.Dimmed, f.State.Tray, "清空后变暗");
        });

        t.Case("C-01", "剪贴板监听默认关闭", () =>
        {
            var f = new Fixture();
            f.Start();
            Check.False(f.State.ClipboardEnabled, "opt-in");
            var r = f.Send(new SetClipboardEnabled(true));
            Check.True(r.Has(CommandKind.RegisterClipboardListener), "开启时注册消息驱动监听");
        });

        t.Case("V-34", "新图入绳与展开都重置 scroll", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new SetKeepOnLine(25));
            for (int i = 1; i <= 10; i++) f.Send(new NewItem(Fixture.P(i)));
            f.Send(new Scroll(500, true));
            Check.Equal(500, f.State.Scroll, "已滚动");
            f.Send(new NewItem(Fixture.P(11)));
            Check.Equal(0, f.State.Scroll, "新图后回到最新端");
        });

        t.Case("V-35", "非精确滚动 ×12 且夹在上限内", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new SetKeepOnLine(25));
            for (int i = 1; i <= 10; i++) f.Send(new NewItem(Fixture.P(i)));
            f.Send(new Scroll(10, false));
            Check.Equal(120, f.State.Scroll, "×12");
            f.Send(new Scroll(9999, true));
            Check.Equal(696, f.State.Scroll, "clamp 到 (10−6)×174");
        });

        t.Case("V-36", "靠边渐隐 min(x, w−x)/90", () =>
        {
            Check.Near(0.5, Layout.EdgeOpacity(45, 1280), 0.001, "45 DIP");
            Check.Near(1.0, Layout.EdgeOpacity(90, 1280), 0.001, "90 DIP 之后不渐隐");
            Check.Near(0.0, Layout.EdgeOpacity(-5, 1280), 0.001, "越界");
        });

        t.Case("V-27b", "同时最多 2 个飞行动画", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            int flights = 0, direct = 0;
            for (int i = 1; i <= 3; i++)
            {
                var r = f.Send(new NewItem(Fixture.P(i)));
                flights += r.Count(CommandKind.StartFlight);
                direct += r.Count(CommandKind.ReloadThumbnail);
            }
            Check.Equal(2, flights, "前两张飞入");
            Check.Equal(1, direct, "第三张直接落位");
        });

        t.Case("V-29", "系统截图静音、复制图片响提示音", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            var a = f.Send(new NewItem(Fixture.P(1), ItemOrigin.Screenshot));
            Check.False(a.Has(CommandKind.PlaySound), "系统截图已有快门声");
            var b = f.Send(new NewItem(Fixture.P(2), ItemOrigin.Clipboard));
            Check.True(b.Has(CommandKind.PlaySound), "复制图片补提示音");
            Check.Equal("Tink", b.Arg(CommandKind.PlaySound), "Tink 0.35");
        });

        t.Case("V-30", "减少动态效果时跳过飞入", () =>
        {
            var f = new Fixture(configure: e => e.ReduceMotion = true);
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            var r = f.Send(new NewItem(Fixture.P(1)));
            Check.False(r.Has(CommandKind.StartFlight), "不飞入");
            Check.True(r.Has(CommandKind.ReloadThumbnail), "直接落位");
        });

        t.Case("A-06", "首次运行欢迎并 pin 5 秒", () =>
        {
            var f = new Fixture();
            var start = f.Send(new ApplicationStarted(string.Empty, true));
            Check.True(start.Has(CommandKind.ShowWelcome), "显示欢迎");
            Check.True(f.State.WelcomePinned, "绳被 pin 住");
            Check.True(f.State.IsRevealed, "强制展开");
            f.Advance(4999);
            Check.True(f.State.WelcomePinned, "5 秒内不收起");
            f.Advance(1);
            Check.False(f.State.WelcomePinned, "5 秒后解除");
        });

        t.Case("A-07", "首次询问接管截图在 1.2 秒后且只问一次", () =>
        {
            var f = new Fixture();
            f.Send(new ApplicationStarted(string.Empty, true));
            var before = f.Advance(1199);
            Check.False(before.Has(CommandKind.ShowInboxOffer), "1.2 秒前不弹");
            var at = f.Advance(1);
            Check.True(at.Has(CommandKind.ShowInboxOffer), "1.2 秒弹出");
            var again = f.Advance(10000);
            Check.Equal(0, again.Count(CommandKind.ShowInboxOffer), "不再重复询问");
        });

        t.Case("A-04", "退出时注销热键、还原设置、销毁窗口", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new NewItem(Fixture.P(1)));
            var r = f.Send(new ApplicationExiting());
            Check.True(r.Has(CommandKind.UnregisterHotKey), "注销热键");
            Check.True(r.Has(CommandKind.RestoreSettings), "还原设置");
            Check.True(r.Has(CommandKind.DestroyWindow), "销毁窗口");
            Check.False(f.State.IsPresent, "无残留窗口");
        });

        t.Case("K-02", "Ctrl+Z 只在线内临时借用", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new TakeDownAll());
            var inside = f.Send(new PointerMoved(640, 100));
            Check.True(inside.Has(CommandKind.RegisterTransientKey), "线内且有撤销步骤才注册");
            var outside = f.Send(new PointerMoved(640, 400));
            Check.True(outside.Has(CommandKind.UnregisterTransientKey), "离开即注销");
        });

        t.Case("F-10", "额外监视文件夹上限 3 且去重", () =>
        {
            var f = new Fixture();
            f.Start();
            for (int i = 1; i <= 3; i++) f.Send(new AddWatchedFolder($@"C:\d{i}"));
            Check.Equal(3, f.State.ExtraFolders.Count, "三个成功");
            Check.True(f.Send(new AddWatchedFolder(@"C:\d4")).Has(CommandKind.Beep), "第四个蜂鸣");
            Check.True(f.Send(new AddWatchedFolder(@"C:\d1")).Has(CommandKind.Beep), "重复路径蜂鸣");
        });

        t.Case("D-07", "角标语义随来源变化", () =>
        {
            Check.Equal(CornerAction.Trash, Line.CornerActionFor(new Pegged { InOwnDirectory = true }), "自有目录→回收站");
            Check.Equal(CornerAction.Hide, Line.CornerActionFor(new Pegged { InOwnDirectory = false }), "其他位置→仅下线");
            Check.Equal(CornerAction.Unpin, Line.CornerActionFor(new Pegged { Kept = true }), "固定→取消固定");
            Check.Equal(CornerAction.Hide, Line.CornerActionFor(new Pegged { ByHand = true, Kept = true }), "手动挂上→仅下线");
        });

        t.Case("V-37", "丢弃后 0.6 秒且指针离开才闭合空位", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            f.Send(new TakeDownAll());
            Check.Equal(1, f.State.Items.Count, "先保留空位");
            f.Advance(599);
            Check.Equal(1, f.State.Items.Count, "599 ms 不闭合");
            f.Advance(1);
            Check.Equal(0, f.State.Items.Count, "600 ms 闭合");
        });

        t.Case("V-32", "绳上保留四档与非法值", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetKeepOnLine(25));
            Check.Equal(25, f.State.MaxItems, "25 张");
            f.Send(new SetKeepOnLine(100));
            Check.Equal(100, f.State.MaxItems, "100 张");
            f.Send(new SetKeepOnLine(0));
            Check.Equal(6, f.State.MaxItems, "放得下的数量");
            Check.True(f.Send(new SetKeepOnLine(37)).Has(CommandKind.Beep), "非法值蜂鸣");
        });

        t.Case("P-05", "检查更新只发浏览器命令，应用不联网", () =>
        {
            var f = new Fixture();
            f.Start();
            var r = f.Send(new CheckForUpdates());
            Check.Equal(1, r.Commands.Count, "只有一条命令");
            Check.True(r.Has(CommandKind.OpenBrowser), "交给浏览器");
            Check.Contains("lang=zh-Hans", r.Arg(CommandKind.OpenBrowser), "带版本与语言");
        });

        t.Case("G-17", "取下左右邻居只作用于普通照片", () =>
        {
            var line = new Line(1280);
            for (int i = 1; i <= 5; i++) line.Hang(new Pegged { Path = $"{i}.png" });
            var middle = line.Items[2];
            Check.Equal(2, line.NeighbourIds(middle.Id, true).Count(), "左侧 2 张");
            Check.Equal(2, line.NeighbourIds(middle.Id, false).Count(), "右侧 2 张");
            line.SetPinned(line.Items[0].Id, true);
            Check.Equal(1, line.NeighbourIds(middle.Id, true).Count(), "固定项不参与");
        });

        t.Case("D-04", "拿在手上不是副本，绳上留淡影", () =>
        {
            var line = new Line(1280);
            var item = new Pegged { Path = "a.png" };
            line.Hang(item);
            line.SetHeld(item.Id, true);
            Check.Equal(1, line.HeldCount, "一张被拿在手上");
            Check.Equal(1, line.Items.Count, "绳上仍留占位");
            Check.True(line.Items[0].Held, "淡影标记");
            line.SetHeld(item.Id, false);
            Check.Equal(0, line.HeldCount, "放回后清除");
        });

        t.Case("F-11", "外部删除文件对应卡片自动掉落", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            var r = f.Send(new FileDisappeared(Fixture.P(1)));
            Check.True(r.Has(CommandKind.StartFall), "触发下落");
            Check.Equal(0, f.State.LiveCount, "卡片下线");
        });

        t.Case("K-06", "默认快捷键为 Ctrl+Alt+T", () =>
        {
            var f = new Fixture();
            var r = f.Start();
            Check.Equal("Ctrl+Alt+T", f.State.Shortcut, "⌃⌥T 映射到 Windows");
            Check.Equal("Ctrl+Alt+T", r.Arg(CommandKind.RegisterHotKey), "启动时注册");
        });
    }
}
