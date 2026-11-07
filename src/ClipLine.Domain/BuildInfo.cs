namespace ClipLine.Domain;

/// <summary>
/// 发布版本号唯一来源。GitHub Release 的 tag 必须是 "v" + Version；
/// MSIX Identity Version 与便携包文件名由 tools/build-msix.ps1 从这里派生。
/// 自动更新用 UpdateVersion.IsNewer 比较 Release tag 与这个值。
/// </summary>
public static class BuildInfo
{
    public const string Version = "1.1.0";
    public const string MsixVersion = "1.1.0.0";
    public const string ReleaseRepo = "dk900912/ClipLine";
}
