using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Tests;

/// <summary>
/// 方案 D 自动更新的门面状态机用例。全部走唯一接缝 AppFacade：
/// 离散事件进、状态快照 + 命令出；网络 / 下载 / 安装都用假事件冒充，不碰真实 Release。
/// </summary>
public sealed class UpdateTests : ITestSuite
{
    private const string NewVersion = "1.0.1";
    private const string AssetUrl = "http://127.0.0.1:8791/assets/ClipLine_1.0.1.0_x64_portable.zip";
    private const string ShaUrl = AssetUrl + ".sha256";

    public void Register(TestRunner t)
    {
        t.Case("U-01", "版本号纯函数比较：v 前缀 / 四段 / 预发布 / 非法值", () =>
        {
            Check.True(UpdateVersion.IsNewer("1.0.1", "1.0.0"), "补丁版本更新");
            Check.True(UpdateVersion.IsNewer("v1.1.0", "1.0.9"), "v 前缀 + 次版本");
            Check.True(UpdateVersion.IsNewer("1.0.0.1", "1.0.0"), "第四段也算");
            Check.True(UpdateVersion.IsNewer("1.2.0-beta.2", "1.1.9"), "预发布后缀被忽略");
            Check.False(UpdateVersion.IsNewer("1.0.0", "1.0.0.0"), "相同版本不更新");
            Check.False(UpdateVersion.IsNewer("0.9.9", "1.0.0"), "不接受降级");
            Check.False(UpdateVersion.TryParse("not-a-version", out _), "非法版本被拒");
        });

        t.Case("U-02", "手动检查只发一条 RunUpdateCheck(manual)", () =>
        {
            var f = new Fixture(); f.Start();
            var r = f.Send(new CheckForUpdates());
            Check.Equal(1, r.Commands.Count, "只有一条命令");
            Check.Equal("manual", r.Arg(CommandKind.RunUpdateCheck), "标记为手动");
            Check.Equal(UpdatePhase.Checking, r.State.Update.Phase, "状态检查中");
            Check.Equal("1.0.0", r.State.Update.CurrentVersion, "当前版本来自环境");
        });

        t.Case("U-03", "启动后自动检查：失败必须静默且不重试", () =>
        {
            var f = new Fixture(); f.Start();
            var r = f.Send(new AutoUpdateTick());
            Check.Equal("auto", r.Arg(CommandKind.RunUpdateCheck), "标记为自动");
            var failed = f.Send(new UpdateCheckCompleted(null, null, null, "离线"));
            Check.Equal(UpdatePhase.Offline, failed.State.Update.Phase, "进入离线态");
            Check.Equal(0, failed.Commands.Count, "离线不弹任何提示");
            var later = f.Advance(60_000);
            Check.True(!later.Has(CommandKind.RunUpdateCheck), "不安排重试 / 不轮询");
        });

        t.Case("U-04", "手动检查失败时给出一条明确提示", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new CheckForUpdates());
            var r = f.Send(new UpdateCheckCompleted(null, null, null, "连接超时"));
            Check.True(r.Has(CommandKind.ShowUpdateNotice), "有提示");
            Check.Contains("连接超时", r.Arg(CommandKind.ShowUpdateNotice), "提示含原因");
        });

        t.Case("U-05", "已是最新版本：不下载、不安装", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new CheckForUpdates());
            var r = f.Send(new UpdateCheckCompleted("1.0.0", AssetUrl, ShaUrl, null));
            Check.Equal(UpdatePhase.UpToDate, r.State.Update.Phase, "已是最新");
            Check.True(!r.Has(CommandKind.DownloadUpdate), "没有下载命令");
            Check.True(r.Has(CommandKind.ShowUpdateNotice), "手动检查有反馈");
        });

        t.Case("U-06", "发现新版本：自动下载且描述符含版本/地址/校验和", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new AutoUpdateTick());
            var r = f.Send(new UpdateCheckCompleted("v" + NewVersion, AssetUrl, ShaUrl, null));
            Check.Equal(UpdatePhase.Available, r.State.Update.Phase, "进入可用态");
            Check.Equal("v" + NewVersion, r.State.Update.AvailableVersion, "记录目标版本");
            string descriptor = r.Arg(CommandKind.DownloadUpdate) ?? string.Empty;
            string[] parts = descriptor.Split('\u001f');
            Check.Equal(3, parts.Length, "三段描述符");
            Check.Equal("v" + NewVersion, parts[0], "版本");
            Check.Equal(AssetUrl, parts[1], "资产地址");
            Check.Equal(ShaUrl, parts[2], "校验和地址");
        });

        t.Case("U-07", "下载进度只改状态快照，不产生新命令", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new CheckForUpdates());
            f.Send(new UpdateCheckCompleted(NewVersion, AssetUrl, ShaUrl, null));
            var mid = f.Send(new UpdateProgress(47));
            Check.Equal(UpdatePhase.Downloading, mid.State.Update.Phase, "下载中");
            Check.Equal(47, mid.State.Update.Progress, "进度 47%");
            Check.Equal(0, mid.Commands.Count, "不产生命令");
            var done = f.Send(new UpdateProgress(100));
            Check.Equal(UpdatePhase.Verifying, done.State.Update.Phase, "进入校验");
        });

        t.Case("U-08", "下载或校验失败：不安装，只提示并回滚为失败态", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new CheckForUpdates());
            f.Send(new UpdateCheckCompleted(NewVersion, AssetUrl, ShaUrl, null));
            var r = f.Send(new UpdateDownloadCompleted(string.Empty, "SHA-256 不匹配"));
            Check.Equal(UpdatePhase.Failed, r.State.Update.Phase, "失败态");
            Check.True(!r.Has(CommandKind.InstallUpdate), "绝不安装未校验的包");
            Check.True(r.Has(CommandKind.ShowUpdateNotice), "有失败提示");
        });

        t.Case("U-09", "校验通过即自动安装（方案 D 全自动）", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new CheckForUpdates());
            f.Send(new UpdateCheckCompleted(NewVersion, AssetUrl, ShaUrl, null));
            string pkg = @"C:\update\ClipLine_1.0.1.0_x64_portable.zip";
            var r = f.Send(new UpdateDownloadCompleted(pkg, null));
            Check.Equal(UpdatePhase.Ready, r.State.Update.Phase, "就绪");
            Check.Equal(pkg, r.Arg(CommandKind.InstallUpdate), "自动发出安装命令");
            Check.Equal(pkg, r.State.Update.LocalPackage, "快照记录本地包");
        });

        t.Case("U-10", "安装失败给出明确原因，不假装成功", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new CheckForUpdates());
            f.Send(new UpdateCheckCompleted(NewVersion, AssetUrl, ShaUrl, null));
            f.Send(new UpdateDownloadCompleted("pkg.zip", null));
            var r = f.Send(new UpdateInstallCompleted(false, "证书不受信任"));
            Check.Equal(UpdatePhase.Failed, r.State.Update.Phase, "失败态");
            Check.Contains("证书不受信任", r.Arg(CommandKind.ShowUpdateNotice), "含原因");
        });

        t.Case("U-11", "安装成功进入 Installing，供壳层退出并重启", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new AutoUpdateTick());
            f.Send(new UpdateCheckCompleted(NewVersion, AssetUrl, ShaUrl, null));
            f.Send(new UpdateDownloadCompleted("pkg.zip", null));
            var r = f.Send(new UpdateInstallCompleted(true, null));
            Check.Equal(UpdatePhase.Installing, r.State.Update.Phase, "安装中");
            Check.True(r.Has(CommandKind.ShowUpdateNotice), "提示即将重启");
        });

        t.Case("U-12", "检查/下载期间重复点击不会并发触发", () =>
        {
            var f = new Fixture(); f.Start();
            f.Send(new CheckForUpdates());
            var again = f.Send(new CheckForUpdates());
            Check.Equal(0, again.Commands.Count, "检查中忽略重复点击");
            f.Send(new UpdateCheckCompleted(NewVersion, AssetUrl, ShaUrl, null));
            var againDownload = f.Send(new CheckForUpdates());
            Check.Equal(0, againDownload.Commands.Count, "下载中忽略重复点击");
        });

        t.Case("U-13", "全流程命令序列：检查→下载→安装，无第二条接缝", () =>
        {
            var f = new Fixture(); f.Start();
            var chain = new List<CommandKind>();
            chain.AddRange(f.Send(new CheckForUpdates()).Commands.Select(c => c.Kind));
            chain.AddRange(f.Send(new UpdateCheckCompleted(NewVersion, AssetUrl, ShaUrl, null))
                .Commands.Select(c => c.Kind));
            chain.AddRange(f.Send(new UpdateDownloadCompleted("pkg.zip", null))
                .Commands.Select(c => c.Kind));
            chain.AddRange(f.Send(new UpdateInstallCompleted(true, null))
                .Commands.Select(c => c.Kind));
            Check.True(chain.Contains(CommandKind.RunUpdateCheck), "有检查");
            Check.True(chain.Contains(CommandKind.DownloadUpdate), "有下载");
            Check.True(chain.Contains(CommandKind.InstallUpdate), "有安装");
            Check.Equal(1, chain.Count(c => c == CommandKind.RunUpdateCheck), "检查恰好一次");
            Check.Equal(1, chain.Count(c => c == CommandKind.InstallUpdate), "安装恰好一次");
        });
    }
}
