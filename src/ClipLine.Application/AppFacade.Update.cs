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
    private bool _downloadCancelled;

    private string ChannelName() => _env.IsPackaged ? "msix" : "portable";

    /// <summary>手动点「检查更新…」与启动后一次性自动检查共用同一条状态机。</summary>
    private void OnUpdateCheck(bool manual)
    {
        // 已下载待安装时再点「检查更新…」不应重新下载，只把面板重新叫出来。
        if (_update.Phase == UpdatePhase.Ready)
        {
            _update = _update with
            {
                Manual = manual || _update.Manual,
                Deferred = false,
                Message = "新版本已下载，确认后安装",
            };
            return;
        }
        // 只有终态能重新发起检查；Available 是「已下令下载」到平台回灌之间的瞬态，
        // 此时再点一次会产生第二个并发下载。
        if (_update.Phase is not (UpdatePhase.Idle
            or UpdatePhase.UpToDate
            or UpdatePhase.Failed
            or UpdatePhase.Offline)) return;

        _downloadCancelled = false;
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
        Emit(CommandKind.ShowUpdateNotice, "发现新版本 " + e.Version + "，正在后台下载…");
        // 下载仍然后台自动进行；安装与重启必须等用户确认（P0-3）。
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
        if (_downloadCancelled)
        {
            // 取消后平台回灌的「任务已取消」不再覆盖用户看到的终态。
            _downloadCancelled = false;
            return;
        }
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
            Message = "新版本已下载，确认后安装",
        };
        // 不再自动安装：面板出现，由用户点「立即重启更新」。
        if (_update.Manual)
            Emit(CommandKind.ShowUpdateNotice,
                "新版本 " + _update.AvailableVersion + " 已下载，打开更新面板完成安装");
    }

    /// <summary>更新面板的「立即重启更新」：唯一允许进入安装的入口。</summary>
    private void OnUpdateInstallConfirmed()
    {
        if (_update.Phase != UpdatePhase.Ready || string.IsNullOrEmpty(_update.LocalPackage)) return;
        _update = _update with { Message = "正在安装更新…", Deferred = false };
        Emit(CommandKind.InstallUpdate, _update.LocalPackage);
    }

    private void OnUpdateDeferred()
    {
        if (_update.Phase is not (UpdatePhase.Ready or UpdatePhase.Failed
            or UpdatePhase.UpToDate or UpdatePhase.Offline)) return;
        _update = _update with { Deferred = true, Message = "已推迟到下次启动" };
    }

    private void OnCancelUpdateDownload()
    {
        if (_update.Phase is not (UpdatePhase.Available or UpdatePhase.Downloading
            or UpdatePhase.Verifying)) return;
        _downloadCancelled = true;
        _update = _update with { Phase = UpdatePhase.Failed, Progress = 0, Message = "已取消下载" };
        Emit(CommandKind.CancelUpdateDownload);
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
