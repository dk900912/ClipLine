using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>P4：预览 / 标注 / 修剪 / 拿在手上 / 右键扩展。</summary>
public sealed class P4Tests : ITestSuite
{
    public void Register(TestRunner t)
    {
        t.Case("K-04", "快速查看可用 ← → 浏览整条绳并循环", () =>
        {
            var model = new QuickLookModel();
            model.Open(0, 3);
            Check.True(model.IsOpen, "已打开");
            model.Apply(QuickLookAction.Next, 3);
            Check.Equal(1, model.Index, "下一张");
            model.Apply(QuickLookAction.Next, 3);
            model.Apply(QuickLookAction.Next, 3);
            Check.Equal(0, model.Index, "末尾循环回第一张");
            model.Apply(QuickLookAction.Previous, 3);
            Check.Equal(2, model.Index, "向前循环");
            model.Apply(QuickLookAction.Close, 3);
            Check.False(model.IsOpen, "关闭");
        });

        t.Case("E-01", "标注保存按原格式重新编码", () =>
        {
            Check.True(MarkupModel.NeedsRecode("jpg"), "JPEG 需要重编码");
            Check.Equal("jpeg", MarkupModel.TargetFormat("JPG"), "格式归一");
            Check.False(MarkupModel.NeedsRecode("png"), "PNG 直接写回");
            Check.Equal("tiff", MarkupModel.Normalize(".TIF"), "TIFF 归一");
        });

        t.Case("E-01b", "标注文档校验笔画", () =>
        {
            var good = new MarkupDocument("a.png", "png", new[]
            {
                new MarkupStroke(MarkupTool.Pen, new[] { (0.0, 0.0), (1.0, 1.0) }, "#FF0000", 3),
            });
            MarkupModel.Validate(good);
            var bad = new MarkupDocument("a.png", "png", new[]
            {
                new MarkupStroke(MarkupTool.Pen, Array.Empty<(double, double)>(), "#000", 3),
            });
            bool threw = false;
            try { MarkupModel.Validate(bad); } catch (ArgumentException) { threw = true; }
            Check.True(threw, "空笔画被拒绝");
        });

        t.Case("E-03", "修剪临时文件命名与 watcher 忽略规则", () =>
        {
            string temp = TrimModel.TempFileName("mp4");
            Check.True(temp.StartsWith(".trimming-"), "隐藏临时文件前缀");
            Check.True(temp.EndsWith(".mp4"), "保留扩展名");
            Check.True(TrimModel.ShouldIgnoreByWatcher(temp), "watcher 忽略");
            Check.False(TrimModel.ShouldIgnoreByWatcher("normal.mp4"), "普通文件不忽略");
        });

        t.Case("E-03b", "修剪范围夹在视频时长内", () =>
        {
            var r = TrimModel.Clamp(new TrimRange(-100, 99_999, 10_000));
            Check.Equal(0, r.StartMs, "起点夹到 0");
            Check.Equal(10_000, r.EndMs, "终点夹到时长");
            Check.True(r.IsValid, "有效");
            var exact = TrimModel.Clamp(new TrimRange(1000, 5000, 10_000));
            Check.Equal(4000, exact.LengthMs, "长度");
            Check.True(TrimModel.WindowSize(1920, 1080).Width <= 720, "窗口宽上限");
            Check.True(TrimModel.WindowSize(1920, 1080).Height <= 520, "窗口高上限");
        });

        t.Case("D-04b", "拿在手上缩放 0.1×…4× 与最小 60×60", () =>
        {
            Check.Equal(0.1, HeldWindowModel.ClampScale(0.01), "下限");
            Check.Equal(4.0, HeldWindowModel.ClampScale(99), "上限");
            Check.True(HeldWindowModel.ApplyScale(10, 10, 0.1).Width >= 60, "最小边长 60");
            var initial = HeldWindowModel.InitialSize(4000, 3000, 1920, 1080);
            Check.True(initial.Width <= 1920 * 0.7 + 1, "初始宽不超过可见区 70%");
            Check.True(initial.Height <= 1080 * 0.7 + 1, "初始高不超过可见区 70%");
        });

        t.Case("D-04c", "多个浮动窗右下错开 24 DIP，8 个一循环", () =>
        {
            Check.Equal((0, 0), HeldWindowModel.CascadeOffset(0), "第一个不错开");
            Check.Equal((24, 24), HeldWindowModel.CascadeOffset(1), "第二个 24");
            Check.Equal((168, 168), HeldWindowModel.CascadeOffset(7), "第八个");
            Check.Equal((0, 0), HeldWindowModel.CascadeOffset(8), "第九个回到起点");
        });

        t.Case("X-01", "右键服务只接图片、去重、无有效项蜂鸣", () =>
        {
            var accepted = ShellExtensionModel.Accept(new[] { "a.png", "A.PNG", "b.txt", "c.jpg", "" });
            Check.Equal(2, accepted.Count, "两张图片");
            Check.True(ShellExtensionModel.ShouldBeep(0), "一个都没成功时蜂鸣");
            Check.False(ShellExtensionModel.ShouldBeep(2), "有成功不蜂鸣");
        });

        t.Case("E-05", "门面：快速查看可在整条绳上前后浏览", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            for (int i = 1; i <= 3; i++) f.Send(new NewItem(Fixture.P(i)));
            var first = f.Send(new QuickLookCommand(QuickLookAction.Toggle));
            Check.True(first.Has(CommandKind.ShowQuickLook), "打开预览");
            Check.Equal(Fixture.P(1), first.Arg(CommandKind.ShowQuickLook), "第一张");
            Check.Equal("0/3", first.Arg(CommandKind.UpdateQuickLookIndex), "索引");
            var next = f.Send(new QuickLookCommand(QuickLookAction.Next));
            Check.Equal(Fixture.P(2), next.Arg(CommandKind.ShowQuickLook), "下一张");
            var close = f.Send(new QuickLookCommand(QuickLookAction.Close));
            Check.True(close.Has(CommandKind.CloseQuickLook), "关闭");
        });

        t.Case("E-02", "门面：标注保存后按原格式重写并刷新缩略图", () =>
        {
            var f = new Fixture();
            f.Start();
            var r = f.Send(new MarkupSaved(Fixture.P(1), "jpg"));
            Check.True(r.Has(CommandKind.RewriteInOriginalFormat), "JPEG 重新编码");
            Check.Contains("jpeg", r.Arg(CommandKind.RewriteInOriginalFormat), "目标格式");
            Check.True(r.Has(CommandKind.ReloadThumbnail), "立即刷新缩略图");
            var png = f.Send(new MarkupSaved(Fixture.P(2), "png"));
            Check.False(png.Has(CommandKind.RewriteInOriginalFormat), "PNG 免重编码");
        });

        t.Case("E-03c", "门面：修剪范围与 passthrough 原子替换", () =>
        {
            var f = new Fixture();
            f.Start();
            var range = f.Send(new TrimRangeChanged(-50, 20_000, 10_000));
            Check.Equal("0-10000/10000", range.Arg(CommandKind.SetTrimRange), "范围被夹住");
            var committed = f.Send(new TrimCommitted("a.mp4", "mp4"));
            Check.True(committed.Has(CommandKind.ExportTrimPassthrough), "无重编码导出");
            Check.Contains(".trimming-", committed.Arg(CommandKind.ExportTrimPassthrough), "隐藏临时文件");
            Check.True(committed.Has(CommandKind.ReplaceFileAtomically), "原子替换原文件");
        });

        t.Case("D-05", "门面：放回时关闭浮动窗并清淡影", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            var id = f.State.Items[0].Id;
            f.Send(new ContextMenuItem(id, ContextAction.Hold));
            Check.True(f.State.Items[0].Held, "已拿在手上");
            var back = f.Send(new HeldWindowCommand(HeldAction.HangBack, id));
            Check.True(back.Has(CommandKind.CloseHeldWindow), "窗口关闭");
            Check.False(f.State.Items[0].Held, "淡影清除");
        });

        t.Case("D-04d", "门面：浮动窗缩放被夹在 0.1×…4×", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new NewItem(Fixture.P(1)));
            var id = f.State.Items[0].Id;
            var r = f.Send(new HeldWindowCommand(HeldAction.Zoom, id, 99));
            Check.Equal(4.0, r.First(CommandKind.SetHeldWindowScale)!.Number, "上限 4×");
            var small = f.Send(new HeldWindowCommand(HeldAction.Zoom, id, 0.01));
            Check.Equal(0.1, small.First(CommandKind.SetHeldWindowScale)!.Number, "下限 0.1×");
        });

        t.Case("X-02", "门面：手动挂上的图片全部固定且不被满绳挤掉", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            var r = f.Send(new ShellExtensionHang(new[] { "a.png", "b.jpg", "c.txt" }));
            Check.Equal(2, f.State.LiveCount, "只挂两张图片");
            Check.Equal(2, f.State.Items.Count(i => i.Kept), "全部固定");
            Check.False(r.Has(CommandKind.Beep), "有成功项不蜂鸣");
            Check.True(f.State.IsRevealed, "绳自动展开");
            for (int i = 3; i <= 9; i++)
                f.Send(new ShellExtensionHang(new[] { $"x{i}.png" }));
            Check.Equal(9, f.State.LiveCount, "2 + 7 张全部保留，超过容量也不被挤掉");
            Check.Equal(0, f.State.Items.Count(i => i.Falling), "无掉落");
        });

        t.Case("X-01b", "门面：右键服务没有有效图片时蜂鸣", () =>
        {
            var f = new Fixture();
            f.Start();
            var r = f.Send(new ShellExtensionHang(new[] { "a.txt", "b.exe" }));
            Check.True(r.Has(CommandKind.Beep), "蜂鸣");
            Check.Equal(0, f.State.LiveCount, "不挂任何东西");
        });
    }
}
