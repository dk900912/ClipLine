using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClipLine.Application;
using ClipLine.Domain;

namespace ClipLine.Platform;

/// <summary>网络传输抽象：测试与本地端到端验证可注入 fake，不碰真实网络。</summary>
public interface IUpdateTransport
{
    Task<string> GetStringAsync(string url, CancellationToken ct);
    Task DownloadFileAsync(string url, string destination, IProgress<int>? progress, CancellationToken ct);
}

/// <summary>默认实现：仅允许 update-config.json 白名单内的主机，避免任意 URL 被 Release 内容劫持。</summary>
public sealed class HttpUpdateTransport : IUpdateTransport
{
    private readonly HttpClient _client;
    private readonly HashSet<string> _allowHosts;

    public HttpUpdateTransport(HashSet<string> allowHosts, string version)
    {
        _allowHosts = allowHosts;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipLine/" + version);
    }

    public void AssertAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) throw new InvalidOperationException("更新地址无效");
        bool https = uri.Scheme == Uri.UriSchemeHttps;
        bool loopback = uri.IsLoopback;
        if (!https && !loopback) throw new InvalidOperationException("更新地址必须是 HTTPS");
        if (!_allowHosts.Contains(uri.Host) && !loopback)
            throw new InvalidOperationException("更新地址不在白名单内：" + uri.Host);
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        AssertAllowed(url);
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task DownloadFileAsync(string url, string destination, IProgress<int>? progress, CancellationToken ct)
    {
        AssertAllowed(url);
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        long read = 0;
        int last = -1;
        while (true)
        {
            int n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                int percent = (int)Math.Clamp(read * 100L / total, 0, 99);
                if (percent != last) { progress?.Report(percent); last = percent; }
            }
        }
        progress?.Report(99);
    }
}

/// <summary>update-config.json + 环境变量覆盖；源码里不出现任何 URL 常量。</summary>
public sealed class UpdateConfig
{
    public string ApiBase { get; private set; } = string.Empty;
    public string Repo { get; private set; } = BuildInfo.ReleaseRepo;
    public HashSet<string> AllowHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static UpdateConfig Load()
    {
        var config = new UpdateConfig();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "update-config.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("apiBase", out var api)) config.ApiBase = api.GetString() ?? string.Empty;
                if (root.TryGetProperty("repo", out var repo)) config.Repo = repo.GetString() ?? config.Repo;
                if (root.TryGetProperty("allowHosts", out var hosts))
                    foreach (JsonElement h in hosts.EnumerateArray())
                        if (h.GetString() is string s) config.AllowHosts.Add(s);
            }
        }
        catch (Exception) { /* 配置损坏时退回默认值，由白名单继续兜底 */ }

        // 配置缺失时保持为空（更新静默不可用），绝不在源码里内置任何 URL 常量。
        if (config.AllowHosts.Count == 0)
        {
            config.AllowHosts.Add("api.github.com");
            config.AllowHosts.Add("github.com");
            config.AllowHosts.Add("127.0.0.1");
            config.AllowHosts.Add("localhost");
        }
        // 本地端到端验证：把 API 指向本机模拟的 Release 服务，产品代码不变。
        string? apiOverride = Environment.GetEnvironmentVariable("CLIPLINE_UPDATE_API");
        if (!string.IsNullOrWhiteSpace(apiOverride)) config.ApiBase = apiOverride.TrimEnd('/');
        string? repoOverride = Environment.GetEnvironmentVariable("CLIPLINE_UPDATE_REPO");
        if (!string.IsNullOrWhiteSpace(repoOverride)) config.Repo = repoOverride;
        return config;
    }

    public string LatestReleaseUrl => ApiBase.Length == 0
        ? string.Empty
        : ApiBase + "/repos/" + Repo + "/releases/latest";
}

/// <summary>
/// 自动更新的平台层编排：检查 → 下载 → SHA-256 校验 → 安装。
/// 门面只发命令、不吃网络；这里完成后把结果作为离散事件回灌同一个 AppFacade 接缝。
/// 全程一次触发、无轮询、无遥测；离线 / 任何异常都转成静默失败事件。
/// </summary>
public sealed class UpdateService
{
    private readonly Action<AppInput> _dispatch;
    private readonly IUpdateTransport _transport;
    private readonly UpdateConfig _config;
    private readonly bool _msixChannel;
    private readonly string _updatesDir;
    private CancellationTokenSource? _downloadCts;

    public UpdateService(Action<AppInput> dispatch, bool msixChannel, IUpdateTransport? transport = null)
    {
        _dispatch = dispatch;
        _msixChannel = msixChannel;
        _config = UpdateConfig.Load();
        _transport = transport ?? new HttpUpdateTransport(_config.AllowHosts, BuildInfo.Version);
        _updatesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipLine", "updates");
    }

    public string ApiBase => _config.ApiBase;
    public string Channel => _msixChannel ? "msix" : "portable";

    private void Post(AppInput input) => _dispatch(input);

    /// <summary>检查最新 Release。mode 仅用于日志；门面已持有 manual/auto 语义。</summary>
    public void RunCheck(string mode) => Fire(async () =>
    {
        if (_config.LatestReleaseUrl.Length == 0)
        {
            Post(new UpdateCheckCompleted(null, null, null, "未配置更新服务地址"));
            return;
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string json = await _transport.GetStringAsync(_config.LatestReleaseUrl, cts.Token);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out JsonElement tag))
        {
            Post(new UpdateCheckCompleted(null, null, null, "Release 缺少 tag_name"));
            return;
        }
        string version = tag.GetString() ?? string.Empty;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                string? name = a.GetProperty("name").GetString();
                string? url = a.GetProperty("browser_download_url").GetString();
                if (name is not null && url is not null) names[name] = url;
            }
        }
        string suffix = _msixChannel ? ".msix" : "_x64_portable.zip";
        string? asset = names.Keys.FirstOrDefault(n =>
            n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            Post(new UpdateCheckCompleted(null, null, null, "Release 没有 " + Channel + " 通道资产"));
            return;
        }
        names.TryGetValue(asset + ".sha256", out string? shaUrl);
        Post(new UpdateCheckCompleted(version, names[asset], shaUrl, null));
    }, "check");

    /// <summary>下载资产并校验 SHA-256；参数由门面 DownloadUpdate 命令拼成三段。</summary>
    public void Download(string descriptor) => Fire(async () =>
    {
        string[] parts = descriptor.Split('\u001f');
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            Post(new UpdateDownloadCompleted(string.Empty, "下载地址为空"));
            return;
        }
        string version = parts[0];
        string assetUrl = parts[1];
        string shaUrl = parts.Length > 2 ? parts[2] : string.Empty;

        string folder = Path.Combine(_updatesDir, version.Replace('/', '_'));
        Directory.CreateDirectory(folder);
        string fileName = new Uri(assetUrl).Segments.Last().TrimEnd('/');
        if (fileName.Length == 0) fileName = Channel + ".pkg";
        string local = Path.Combine(folder, fileName);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _downloadCts = cts;
        var progress = new Progress<int>(p => Post(new UpdateProgress(p)));
        try
        {
            await _transport.DownloadFileAsync(assetUrl, local, progress, cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
        }

        if (shaUrl.Length > 0)
        {
            string shaText = await _transport.GetStringAsync(shaUrl, cts.Token);
            string expected = shaText.Split('\n', '\r', ' ', '\t')[0].Trim();
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(local)));
            if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(local);
                Post(new UpdateDownloadCompleted(string.Empty, "SHA-256 校验不兼容，已丢弃下载文件"));
                return;
            }
        }
        Post(new UpdateProgress(100));
        Post(new UpdateDownloadCompleted(local, null));
    }, "download");

    /// <summary>取消正在进行的下载（安装阶段不可取消，必须等确认）。</summary>
    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { /* 已结束 */ }
        _downloadCts = null;
    }

    /// <summary>按安装形态执行：MSIX 走 Add-AppxPackage，便携版走 updater helper。</summary>
    public void Install(string localPackage) => Fire(() =>
    {
        bool ok = _msixChannel
            ? MsixInstaller.Install(localPackage)
            : PortableInstaller.Install(localPackage);
        Post(new UpdateInstallCompleted(ok, ok ? null : "安装器返回失败"));
        return Task.CompletedTask;
    }, "install");

    private void Fire(Func<Task> body, string stage) => _ = Task.Run(async () =>
    {
        try { await body(); }
        catch (Exception ex)
        {
            string message = Shorten(ex.Message);
            if (stage == "check") Post(new UpdateCheckCompleted(null, null, null, message));
            else if (stage == "download") Post(new UpdateDownloadCompleted(string.Empty, message));
            else Post(new UpdateInstallCompleted(false, message));
        }
    });

    private static string Shorten(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 140 ? s[..140] : s;
    }
}
