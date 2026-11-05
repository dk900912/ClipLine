using ClipLine.Domain;

namespace ClipLine.Application;

/// <summary>
/// 自动更新状态机（方案 D：双通道全自动）。
/// 仍然是同一个 AppFacade 接缝：离散事件进 → 状态快照 + 外部命令出。
/// 网络、下载、SHA-256 校验、MSIX 安装、便携版自我替换全部在平台层；
/// 这里只决定「何时检查 / 是否下载 / 何时安装 / 何时提示」，因此可用假事件单测。
/// </summary>
public sealed partial class AppFacade
{
    private UpdateSnapshot _update = new();

    private string ChannelName() => _env.IsPackaged ? "msix" : "portable";

    /// <summary>手动点「检查更新…」与启动后一次性自动检查共用同一条状态机。</summary>
    private void OnUpdateCheck(bool manual)
    {
        // 只有终态能重新发起检查；Available / Ready 是「已下令下载 / 已下令安装」
        // 到平台回灌之间的瞬态，此时再点一次会产生第二个并发下载。
        if (_update.Phase is not (UpdatePhase.Idle
            or UpdatePhase.UpToDate
            or UpdatePhase.Failed
            or UpdatePhase.Offline)) return;

        _update = new UpdateSnapshot
        {
            Phase = UpdatePhase.Checking,
            CurrentVersion = _env.Version,
            Channel = ChannelName(),
            Manual = manual,
            Message = "正在检查更新…",
        };
        Emit(CommandKind.RunUpdateCheck, manual ? "manual" : "auto");
    }

    private void OnUpdateCheckCompleted(UpdateCheckCompleted e)
    {
        if (e.Error is not null)
        {
            _update = _update with
            {
                Phase = UpdatePhase.Offline,
                Message = e.Error,
                Progress = 0,
            };
            // 自动检查失败必须静默：离线是常态，不打扰、不重试、不轮询。
            if (_update.Manual) Emit(CommandKind.ShowUpdateNotice, "暂时无法检查更新：" + e.Error);
            return;
        }

        if (e.Version is null || !UpdateVersion.IsNewer(e.Version, _env.Version))
        {
            _update = _update with
            {
                Phase = UpdatePhase.UpToDate,
                Message = "已是最新版本",
                Progress = 0,
            };
            if (_update.Manual) Emit(CommandKind.ShowUpdateNotice, "已是最新版本 " + _env.Version);
            return;
        }

        _update = _update with
        {
            Phase = UpdatePhase.Available,
            AvailableVersion = e.Version,
            Message = "发现新版本 " + e.Version,
        };
        Emit(CommandKind.ShowUpdateNotice, "发现新版本 " + e.Version + "，正在下载…");
        // 方案 D 是全自动：发现新版本即下载，不等待用户二次确认。
        Emit(CommandKind.DownloadUpdate,
            string.Join('\u001f', e.Version, e.AssetUrl ?? string.Empty, e.Sha256Url ?? string.Empty));
    }

    private void OnUpdateProgress(UpdateProgress e)
    {
        if (_update.Phase is not (UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Verifying)) return;
        int percent = Math.Clamp(e.Percent, 0, 100);
        _update = _update with
        {
            Phase = percent >= 100 ? UpdatePhase.Verifying : UpdatePhase.Downloading,
            Progress = percent,
        };
    }

    private void OnUpdateDownloadCompleted(UpdateDownloadCompleted e)
    {
        if (e.Error is not null)
        {
            _update = _update with { Phase = UpdatePhase.Failed, Message = e.Error, Progress = 0 };
            Emit(CommandKind.ShowUpdateNotice, "更新下载失败：" + e.Error);
            return;
        }

        _update = _update with
        {
            Phase = UpdatePhase.Ready,
            LocalPackage = e.LocalPackage,
            Progress = 100,
            Message = "校验通过，准备安装",
        };
        // 校验通过即安装（双通道全自动）。平台层按当前安装形态选择 MSIX / 便携路径。
        Emit(CommandKind.InstallUpdate, e.LocalPackage);
    }

    private void OnUpdateInstallCompleted(UpdateInstallCompleted e)
    {
        if (!e.Success)
        {
            _update = _update with { Phase = UpdatePhase.Failed, Message = e.Error ?? "安装失败" };
            Emit(CommandKind.ShowUpdateNotice, "更新安装失败：" + (e.Error ?? "未知原因"));
            return;
        }

        _update = _update with
        {
            Phase = UpdatePhase.Installing,
            Message = "更新已完成，正在重启…",
        };
        Emit(CommandKind.ShowUpdateNotice, "更新已完成，正在重启 ClipLine…");
    }
}
