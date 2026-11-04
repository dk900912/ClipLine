using System.Text;
using ClipLine.Application;
using ClipLine.Domain;
using ClipLine.Platform;

namespace ClipLine.Tests;

/// <summary>P2：三层捕获、完整性校验、剪贴板过滤与飞入参数。</summary>
public sealed class P2Tests : ITestSuite
{
    private static byte[] Png(bool complete)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange(new byte[16]);
        if (complete) bytes.AddRange(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 });
        return bytes.ToArray();
    }

    public void Register(TestRunner t)
    {
        t.Case("F-07", "支持的图片与录屏扩展名", () =>
        {
            Check.True(CaptureIntegrity.IsImage("a.PNG"), "PNG 大小写不敏感");
            Check.True(CaptureIntegrity.IsImage("a.heic"), "HEIC");
            Check.True(CaptureIntegrity.IsVideo("a.mp4"), "MP4");
            Check.True(CaptureIntegrity.IsVideo("a.mov"), "MOV");
            Check.False(CaptureIntegrity.IsSupported("a.txt"), "文本拒绝");
            Check.Equal(FileKind.Jpeg, CaptureIntegrity.Classify("a.JPEG"), "JPEG 分类");
        });

        t.Case("F-08a", "PNG 末尾 12 字节 IEND + AE426082", () =>
        {
            Check.Equal(IntegrityVerdict.Complete, CaptureIntegrity.VerifyPath("a.png", Png(true), null, 0), "完整 PNG 立即通过");
            Check.Equal(IntegrityVerdict.Incomplete, CaptureIntegrity.VerifyPath("a.png", Png(false), null, 0), "半截 PNG 不上绳");
            Check.Equal(IntegrityVerdict.TooOld, CaptureIntegrity.VerifyPath("a.png", Png(true), null, 600_001), "超过 10 分钟放弃");
        });

        t.Case("F-08b", "JPEG EOI / GIF 尾字节 / WebP RIFF 长度", () =>
        {
            Check.True(CaptureIntegrity.Verify(FileKind.Jpeg, new byte[] { 0xFF, 0xD8, 0x01, 0xFF, 0xD9 }, null), "FF D9");
            Check.False(CaptureIntegrity.Verify(FileKind.Jpeg, new byte[] { 0xFF, 0xD8, 0x01, 0x02 }, null), "缺 EOI");
            Check.True(CaptureIntegrity.Verify(FileKind.Gif, new byte[] { 0x47, 0x49, 0x46, 0x3B }, null), "3B");
            Check.False(CaptureIntegrity.Verify(FileKind.Gif, new byte[] { 0x47, 0x49, 0x46, 0x00 }, null), "未写完");

            var webp = new byte[20];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(webp, 0);
            BitConverter.GetBytes(12u).CopyTo(webp, 4); // 20 − 8 = 12
            Encoding.ASCII.GetBytes("WEBP").CopyTo(webp, 8);
            Check.True(CaptureIntegrity.Verify(FileKind.WebP, webp, null), "RIFF 声明长度与文件一致");
            BitConverter.GetBytes(99u).CopyTo(webp, 4);
            Check.False(CaptureIntegrity.Verify(FileKind.WebP, webp, null), "长度不符说明仍在写");
        });

        t.Case("F-08c", "HEIC / MP4 逐 box 校验链尾", () =>
        {
            var complete = new byte[16];
            complete[3] = 16; // box size = 16（大端）
            Encoding.ASCII.GetBytes("ftyp").CopyTo(complete, 4);
            Check.True(CaptureIntegrity.Verify(FileKind.Heic, complete, null), "单个完整 box");
            var broken = new byte[15];
            broken[3] = 16;
            Check.False(CaptureIntegrity.Verify(FileKind.Heic, broken, null), "box 超出文件末尾");
        });

        t.Case("F-08d", "其他格式以 0.5 秒内大小不变判定", () =>
        {
            Check.False(CaptureIntegrity.Verify(FileKind.Tiff, new byte[] { 1, 2, 3 }, 200), "未满 0.5 秒");
            Check.True(CaptureIntegrity.Verify(FileKind.Tiff, new byte[] { 1, 2, 3 }, 500), "稳定 0.5 秒");
            Check.False(CaptureIntegrity.Verify(FileKind.Other, Array.Empty<byte>(), 500), "空文件");
        });

        t.Case("R-02", "完整性复查 600 秒后放弃并释放内存", () =>
        {
            var tracker = new IntegrityTracker();
            tracker.Track("a.png", 0, 10);
            Check.Equal(1, tracker.Count, "已登记");
            tracker.ReportSize("a.png", 500, 1000);
            Check.Equal(0, tracker.StableForMs("a.png", 1000), "大小变化重置稳定窗口");
            Check.Equal(500, tracker.StableForMs("a.png", 1500), "稳定 0.5 秒");
            var decision = tracker.Tick("a.png", 600_000);
            Check.True(decision.GiveUp, "10 分钟放弃");
            Check.Equal(0, tracker.Count, "从队列移除，不泄漏内存");
        });

        t.Case("F-05", "只认启动时间之后创建的文件", () =>
        {
            var before = new CaptureCandidate("a.png", CaptureSource.WatchFolder, -1);
            var after = new CaptureCandidate("a.png", CaptureSource.WatchFolder, 0);
            Check.False(CaptureRules.Accept(before, 0), "启动前文件只登记不挂");
            Check.True(CaptureRules.Accept(after, 0), "启动后文件可挂");
        });

        t.Case("F-06", "桌面只认真截图、录屏只在自有目录", () =>
        {
            Check.False(CaptureRules.Accept(new CaptureCandidate("a.png", CaptureSource.Desktop, 0)), "桌面普通文件拒绝");
            Check.True(CaptureRules.Accept(new CaptureCandidate("a.png", CaptureSource.Desktop, 0, true)), "带截图标记接受");
            Check.False(CaptureRules.Accept(new CaptureCandidate("a.mp4", CaptureSource.WatchFolder, 0)), "用户文件夹录屏拒绝");
            Check.True(CaptureRules.Accept(new CaptureCandidate("a.mp4", CaptureSource.OwnCaptures, 0)), "自有目录录屏接受");
            Check.True(CaptureRules.Accept(new CaptureCandidate("b.png", CaptureSource.WatchFolder, 0)), "用户文件夹任意图片接受");
        });

        t.Case("F-08e", "门面：不完整文件等待复查，写完后才挂", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            var r = f.Send(new FileCreated(Fixture.P(1), CaptureSource.WatchFolder, 0, false, false, 10));
            Check.True(r.Has(CommandKind.StartIntegrityRetry), "启动 0.5 秒复查");
            Check.Equal(0, f.State.LiveCount, "半截文件不上绳");
            f.Send(new FileSizeChanged(Fixture.P(1), 900));
            f.Advance(500);
            var done = f.Send(new FileIntegrityResolved(Fixture.P(1), true));
            Check.True(done.Has(CommandKind.StopIntegrityRetry), "停止复查");
            Check.Equal(1, f.State.LiveCount, "写完后挂上");
        });

        t.Case("R-01", "系统整体落盘的截图零延迟挂上", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            var r = f.Send(new FileCreated(Fixture.P(1), CaptureSource.OwnScreenshots, 0, false, true, 4096));
            Check.False(r.Has(CommandKind.StartIntegrityRetry), "不进入复查队列");
            Check.Equal(1, f.State.LiveCount, "立即挂上");
        });

        t.Case("F-05b", "同一文件 0.2 秒内的重复事件被防抖", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new FileCreated(Fixture.P(1), CaptureSource.WatchFolder, 0, false, true, 10));
            Check.Equal(1, f.State.LiveCount, "第一次挂上");
            f.Advance(100);
            f.Send(new FileCreated(Fixture.P(1), CaptureSource.WatchFolder, 0, false, true, 10));
            Check.Equal(1, f.State.LiveCount, "100 ms 内重复事件忽略");
            f.Advance(150);
            f.Send(new FileCreated(Fixture.P(2), CaptureSource.WatchFolder, 0, false, true, 10));
            Check.Equal(2, f.State.LiveCount, "新文件正常挂上");
        });

        t.Case("C-03", "剪贴板严格过滤文本 / 文件引用 / 私密标记", () =>
        {
            var png = new ClipboardSnapshot(new[] { "public.png" }, 100, 10, true, "h");
            Check.True(ClipboardFilter.Accept(png, true, out _), "纯图像接受");
            Check.False(ClipboardFilter.Accept(png, false, out _), "监听未开启拒绝");
            foreach (string forbidden in new[] { "Text", "FileDrop", "org.nspasteboard.ConcealedType", "org.nspasteboard.TransientType", "org.nspasteboard.AutoGeneratedType" })
            {
                var mixed = new ClipboardSnapshot(new[] { "public.png", forbidden }, 100, 10, true, "h");
                Check.False(ClipboardFilter.Accept(mixed, true, out _), $"含 {forbidden} 整体忽略");
            }
        });

        t.Case("C-04", "64 MB / 8000 万像素 / 必须可解码", () =>
        {
            var tooBig = new ClipboardSnapshot(new[] { "PNG" }, ClipboardFilter.MaxBytes + 1, 10, true, "h");
            var tooManyPixels = new ClipboardSnapshot(new[] { "PNG" }, 100, ClipboardFilter.MaxPixels + 1, true, "h");
            var broken = new ClipboardSnapshot(new[] { "PNG" }, 100, 10, false, "h");
            Check.False(ClipboardFilter.Accept(tooBig, true, out _), "超过 64 MB");
            Check.False(ClipboardFilter.Accept(tooManyPixels, true, out _), "超过 8000 万像素");
            Check.False(ClipboardFilter.Accept(broken, true, out _), "解码失败");
        });

        t.Case("C-05", "落盘命名与 0600 权限", () =>
        {
            var ts = new DateTime(2026, 11, 4, 12, 30, 45);
            Check.Equal("Clipboard 2026-11-04 at 12.30.45.png", ClipboardFilter.FileName(ts), "基础命名");
            Check.Equal("Clipboard 2026-11-04 at 12.30.45 2.png", ClipboardFilter.FileName(ts, 1), "同名追加 2");
            Check.Equal(384, ClipboardFilter.FileModeOwnerOnly, "0600");
        });

        t.Case("C-01b", "剪贴板监听关闭时完全不响应", () =>
        {
            var f = new Fixture();
            f.Start();
            var r = f.Send(new ClipboardChanged(
                new ClipboardSnapshot(new[] { "PNG" }, 100, 10, true, "h"),
                new DateTime(2026, 11, 4, 12, 0, 0)));
            Check.Equal(0, r.Commands.Count, "默认关闭，零副作用");
        });

        t.Case("C-05b", "开启后落盘 PNG 并挂上绳", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new SetClipboardEnabled(true));
            var ts = new DateTime(2026, 11, 4, 12, 30, 0);
            var r = f.Send(new ClipboardChanged(
                new ClipboardSnapshot(new[] { "public.png" }, 1024, 1000, true, "hash-1"), ts));
            Check.True(r.Has(CommandKind.SaveCopiedPng), "原子落盘命令");
            Check.Contains("Clipboard 2026-11-04 at 12.30.00.png", r.Arg(CommandKind.SaveCopiedPng), "文件名");
            Check.Equal(384, r.First(CommandKind.SaveCopiedPng)!.Number, "0600 权限");
            Check.Equal(1, f.State.LiveCount, "挂上绳");
        });

        t.Case("C-06", "完全相同的图片且文件仍在时不重复挂", () =>
        {
            var f = new Fixture();
            f.Start();
            f.Send(new SetPeekOnNewContent(false));
            f.Send(new SetClipboardEnabled(true));
            var ts = new DateTime(2026, 11, 4, 12, 30, 0);
            var snap = new ClipboardSnapshot(new[] { "PNG" }, 100, 10, true, "same-hash");
            var first = f.Send(new ClipboardChanged(snap, ts));
            // 平台层落盘完成后，假环境里标记该文件确实存在。
            f.Env.ExistingFiles.Add(first.Arg(CommandKind.SaveCopiedPng)!);
            var second = f.Send(new ClipboardChanged(snap, ts));
            Check.False(second.Has(CommandKind.SaveCopiedPng), "重复被忽略");
            Check.Equal(1, f.State.LiveCount, "仍只有一张");
            f.Send(new TakeDownAll());
            var third = f.Send(new ClipboardChanged(snap, ts));
            Check.True(third.Has(CommandKind.SaveCopiedPng), "取下后可再次挂上");
        });

        t.Case("V-26", "飞入参数 0.65s / 30pt 弧线 / 120fps", () =>
        {
            var plan = CaptureFlight.Plan(800, 1.0, false, 0);
            Check.Equal(650, plan.DurationMs, "时长");
            Check.Equal(30, plan.ArcLiftDip, "中途抬高");
            Check.Equal(120, plan.FrameRate, "帧率");
            Check.Equal(800, plan.ThumbnailLimit, "缩略图上限");
            Check.Equal(0.35, plan.GlassFadeStart, "玻璃框渐显起点");
            Check.False(plan.Skipped, "正常飞入");
            Check.True(CaptureFlight.Plan(800, 1.0, false, 2).Skipped, "已有 2 个飞行时直接落位");
            Check.True(CaptureFlight.Plan(800, 1.0, true, 0).Skipped, "减少动效时跳过");
        });

        t.Case("V-28b", "下落 520pt / 0.55s 与角度", () =>
        {
            var fall = CaptureFlight.Fall(2.5, false);
            Check.Equal(520, fall.DistanceDip, "距离");
            Check.Equal(550, fall.DurationMs, "时长");
            Check.Near(Layout.FallTilt(2.5), fall.AngleDegrees, 0.0001, "角度");
            Check.True(CaptureFlight.Fall(0, true).Skipped, "减少动效直接离场");
        });

        t.Case("N-04", "捕获区域推算结果作为飞入起点记录", () =>
        {
            var f = new Fixture();
            f.Start();
            var r = f.Send(new CaptureRegion(100, 200, 640, 480));
            Check.Equal("100,200,640,480", r.Arg(CommandKind.SetFlightOrigin), "主屏坐标");
        });

        t.Case("P2-01", "单一系统缩放换算", () =>
        {
            Check.Near(1.0, DisplayInfo.ScaleFromDpi(96), 0.0001, "100%");
            Check.Near(1.25, DisplayInfo.ScaleFromDpi(120), 0.0001, "125%");
            Check.Near(1.5, DisplayInfo.ScaleFromDpi(144), 0.0001, "150%");
            Check.Near(2.0, DisplayInfo.ScaleFromDpi(192), 0.0001, "200%");
            Check.Equal(10, DisplayInfo.RevealBandDip, "顶部热条 10 DIP");
        });

        t.Case("F-05c", "目录变更解析与 0.2 秒防抖", () =>
        {
            Check.True(FolderWatcher.ShouldSuppress(1000, 1150), "150 ms 抑制");
            Check.False(FolderWatcher.ShouldSuppress(1000, 1300), "300 ms 放行");
            Check.Equal(FolderChange.Added, FolderWatcher.ToChange(1), "ADDED");
            Check.Equal(FolderChange.RenamedNewName, FolderWatcher.ToChange(5), "RENAMED_NEW_NAME");

            byte[] buffer = new byte[64];
            byte[] name = Encoding.Unicode.GetBytes("a.png");
            BitConverter.GetBytes(0u).CopyTo(buffer, 0);
            BitConverter.GetBytes(1u).CopyTo(buffer, 4);
            BitConverter.GetBytes((uint)name.Length).CopyTo(buffer, 8);
            name.CopyTo(buffer, 12);
            var parsed = FolderWatcher.Parse(buffer, 12 + name.Length);
            Check.Equal(1, parsed.Count, "一条记录");
            Check.Equal("a.png", parsed[0].Name, "UTF-16 文件名");
            Check.Equal(FolderChange.Added, parsed[0].Change, "动作");
        });

        t.Case("K-01", "Ctrl+Alt+T 修饰键映射", () =>
        {
            Check.Equal(3u, HotKey.ParseModifiers("Ctrl+Alt+T"), "Ctrl|Alt");
            Check.Equal(2u, HotKey.ParseModifiers("Ctrl+Z"), "Ctrl");
            Check.Equal(7u, HotKey.ParseModifiers("Ctrl+Alt+Shift+S"), "三修饰键");
        });
    }
}
