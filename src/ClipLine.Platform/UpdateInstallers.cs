using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ClipLine.Platform;

/// <summary>
/// 安装调度器：主进程只负责准备好文件、把任务交给一次性 helper，然后退出。
/// 真正的文件替换 / 包注册由系统自带 PowerShell 执行（无需额外 .NET 运行时、不常驻）。
/// </summary>
public static class PortableInstaller
{
    public static bool Install(string zipPath)
    {
        if (!File.Exists(zipPath)) return false;
        try
        {
            string payload = UpdateHelperLauncher.NewWorkDir("payload");
            ZipFile.ExtractToDirectory(zipPath, payload, overwriteFiles: true);
            if (!File.Exists(Path.Combine(payload, "ClipLine.exe"))) return false;
            return UpdateHelperLauncher.Start("portable", payload, null, null);
        }
        catch (Exception) { return false; }
    }
}

public static class MsixInstaller
{
    /// <summary>
    /// 生产路径由 helper 先试 Add-AppxPackage -Path（签名包，需证书被信任 / 生产 CA）；
    /// 失败且机器开启开发者模式时，用预解包的 loose 目录 Add-AppxPackage -Register。
    /// 本机已实测 -Register 可在开发者模式下成功登记，无需管理员与 CA 证书。
    /// </summary>
    public static bool Install(string msixPath)
    {
        if (!File.Exists(msixPath)) return false;
        try
        {
            string loose = UpdateHelperLauncher.NewWorkDir("loose");
            // MSIX 是 OPC(ZIP) 容器，标准 ZipFile 即可展开，不依赖 Windows SDK 的 makeappx。
            ZipFile.ExtractToDirectory(msixPath, loose, overwriteFiles: true);
            if (!File.Exists(Path.Combine(loose, "AppxManifest.xml"))) return false;
            return UpdateHelperLauncher.Start("msix", null, msixPath, loose);
        }
        catch (Exception) { return false; }
    }
}

internal static class UpdateHelperLauncher
{
    internal static string NewWorkDir(string prefix) =>
        Path.Combine(Path.GetTempPath(), "clipline-update",
            prefix + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));

    internal static bool Start(string mode, string? source, string? package, string? loose)
    {
        try
        {
            string scriptPath = Path.Combine(AppContext.BaseDirectory, "update-helper.ps1");
            if (!File.Exists(scriptPath)) return false;
            string script = File.ReadAllText(scriptPath);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string target = AppContext.BaseDirectory.TrimEnd('\\', '/');

            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["CLIPLINE_UPDATE_MODE"] = mode;
            psi.Environment["CLIPLINE_UPDATE_PID"] = Environment.ProcessId.ToString();
            psi.Environment["CLIPLINE_UPDATE_TARGET"] = target;
            psi.Environment["CLIPLINE_UPDATE_EXE"] = "ClipLine.exe";
            psi.Environment["CLIPLINE_UPDATE_SOURCE"] = source ?? string.Empty;
            psi.Environment["CLIPLINE_UPDATE_PACKAGE"] = package ?? string.Empty;
            psi.Environment["CLIPLINE_UPDATE_LOOSE"] = loose ?? string.Empty;
            return Process.Start(psi) is not null;
        }
        catch (Exception) { return false; }
    }
}
