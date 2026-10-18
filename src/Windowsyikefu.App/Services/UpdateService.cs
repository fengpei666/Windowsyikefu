using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Windowsyikefu.Services;

/// <summary>
/// 一次更新检查的结果。数据全部来自 GitHub Releases，
/// 界面只关心「有没有新版、新版多少、去哪下载、是否必须更新、更新了什么」。
/// </summary>
public sealed class UpdateInfo
{
    /// <summary>远端是否存在比当前更新的版本</summary>
    public bool HasUpdate { get; set; }

    /// <summary>最新版本号，已去掉 tag 上的 v 前缀，形如 1.1.0</summary>
    public string? Version { get; set; }

    /// <summary>安装包地址：安装版用它做静默升级</summary>
    public string? SetupUrl { get; set; }

    /// <summary>免安装单文件地址：免安装版用它替换自身</summary>
    public string? PortableUrl { get; set; }

    /// <summary>Release 页面地址，两种包都拿不到时的最后兜底</summary>
    public string? PageUrl { get; set; }

    /// <summary>是否强制更新（Release 标题带 [强制] 前缀，或 tag 以 -force 结尾）</summary>
    public bool Force { get; set; }

    /// <summary>更新说明，取自 Release 正文</summary>
    public string? UpdateLog { get; set; }

    /// <summary>默认下载地址（优先安装包），留给「用浏览器下载」兜底</summary>
    public string? DownloadUrl => SetupUrl ?? PortableUrl ?? PageUrl;

    /// <summary>有更新、且拿得到下载地址，才谈得上能更新</summary>
    public bool CanDownload => HasUpdate && !string.IsNullOrWhiteSpace(DownloadUrl);

    /// <summary>
    /// 按当前是谁在跑挑地址：安装版拿安装包（能静默升级并自动重启），
    /// 免安装版拿单文件（替换自己即可，不必变成安装版）。
    /// </summary>
    public string? ResolveUrl(bool installed) =>
        installed
            ? SetupUrl ?? PortableUrl ?? PageUrl
            : PortableUrl ?? SetupUrl ?? PageUrl;
}

/// <summary>更新说明里一行的类型，界面据此决定用什么样式渲染</summary>
public enum UpdateLogKind
{
    /// <summary>小节标题（markdown 的 ### 行）</summary>
    Heading,

    /// <summary>条目（- 开头的行），渲染成带圆点的列表</summary>
    Bullet,

    /// <summary>普通段落</summary>
    Paragraph,
}

/// <summary>更新说明拆分后的一行文本</summary>
public sealed record UpdateLogLine(UpdateLogKind Kind, string Text);

/// <summary>下载进度：Total 为 -1 表示服务端没给长度，此时按已下载字节显示</summary>
public readonly record struct DownloadProgress(long Received, long Total)
{
    /// <summary>是否知道总大小</summary>
    public bool HasTotal => Total > 0;

    /// <summary>百分比，0~100；不知道总大小时返回 0，界面改用不定进度</summary>
    public double Percent => HasTotal ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;
}

/// <summary>
/// 版本更新检查：直接问 GitHub Releases，不再依赖自建服务器。
///
/// 取 latest release 的 tag 当版本号、正文当更新说明、附件里的安装包当下载地址。
/// 只要发一个 Release 就等于发了新版，自己的服务器 / 域名出什么事都不影响老客户端。
/// </summary>
public static class UpdateService
{
    /// <summary>发布仓库：更新信息与安装包都从这里取</summary>
    private const string Owner = "fengpei666";
    private const string Repo = "Windowsyikefu";

    private const string ReleaseApi =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private const string ReleasePage =
        $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>永远指向最新版安装包的固定地址，附件列表取不到时拿它兜底</summary>
    private const string LatestSetupUrl =
        $"https://github.com/{Owner}/{Repo}/releases/latest/download/kefu_setup.exe";

    /// <summary>优先推荐的安装包</summary>
    private const string SetupAsset = "kefu_setup.exe";

    /// <summary>安装包缺失时退回免安装版</summary>
    private const string PortableAsset = "Windowsyikefu.exe";

    /// <summary>更新说明最多渲染这么多行，防止一次发版写上千字把窗口撑满</summary>
    private const int MaxLogLines = 30;

    /// <summary>当前客户端版本，形如 1.0.0</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 拉一次最新版本信息。网络不通 / 仓库没有 Release 时抛出，
    /// 由调用方决定是静默跳过还是提示用户。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        // GitHub 的 API 不接受空 User-Agent，不带会被直接拒（403）
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            $"Windowsyikefu/{CurrentVersion}");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "application/vnd.github+json");

        var json = await http.GetStringAsync(ReleaseApi, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 草稿和预发布都不该推给普通用户
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

        var version = NormalizeTag(tag);
        if (version == null) return null;

        var hasUpdate = IsNewerVersion(version, CurrentVersion);
        var (setup, portable) = PickDownloadUrls(root);

        return new UpdateInfo
        {
            HasUpdate = hasUpdate,
            Version = version,
            Force = hasUpdate && IsForceRelease(name, tag),
            UpdateLog = string.IsNullOrWhiteSpace(body) ? null : body.Trim(),
            SetupUrl = hasUpdate ? setup : null,
            PortableUrl = hasUpdate ? portable : null,
            PageUrl = hasUpdate ? page : null,
        };
    }

    /// <summary>
    /// 把 Release 正文解析成能排版的行：小节标题、条目、段落。
    /// 表格和分隔线直接丢弃 —— 在纯文本窗口里它们只会变成一堆竖线。
    /// </summary>
    public static IReadOnlyList<UpdateLogLine> ParseLog(string? body)
    {
        var lines = new List<UpdateLogLine>();
        if (string.IsNullOrWhiteSpace(body)) return lines;

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // markdown 表格行（| a | b |）、水平分隔线（--- / *** / ___）
            if (line[0] == '|' || line.All(c => c is '-' or '*' or '_' or ' ')) continue;

            var kind = UpdateLogKind.Paragraph;

            if (line[0] == '#')
            {
                kind = UpdateLogKind.Heading;
                line = line.TrimStart('#').Trim();
            }
            else if (line.Length > 1 && (line[0] is '-' or '*' or '+') && line[1] == ' ')
            {
                kind = UpdateLogKind.Bullet;
                line = line[1..].Trim();
            }
            else if (line[0] == '>')
            {
                line = line.TrimStart('>').Trim();
            }

            line = CleanInline(line);
            if (line.Length == 0) continue;

            lines.Add(new UpdateLogLine(kind, line));
            if (lines.Count >= MaxLogLines) break;
        }

        return lines;
    }

    /// <summary>用系统默认方式打开下载地址（浏览器下载）</summary>
    public static bool OpenDownload(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------- 应用内更新

    /// <summary>
    /// 当前是否跑在安装版里（安装目录带 unins000.exe）。
    /// 免安装版是单个 exe，旁边没有卸载程序，替换方式不一样。
    /// </summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// 把更新包下到临时目录，边下边报进度。
    /// 每次先清空目录 —— 宁可重下，也不要用上次中断留下的半个文件。
    /// </summary>
    public static async Task<string> DownloadPackageAsync(
        string url, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Windowsyikefu-update");
        Directory.CreateDirectory(dir);

        foreach (var stale in Directory.GetFiles(dir))
        {
            try { File.Delete(stale); } catch { /* 删不掉就留着，下面按 URL 里的文件名另存 */ }
        }

        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(name)) name = SetupAsset;

        var target = Path.Combine(dir, name);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"Windowsyikefu/{CurrentVersion}");

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            progress?.Report(new DownloadProgress(received, total));
        }

        return target;
    }

    /// <summary>
    /// 把下载好的包跑起来。安装版走 Inno 的静默模式：自动关掉本程序、
    /// 装完再自动重启新版；免安装版改用替换自身 exe 的方式。
    /// </summary>
    public static bool LaunchUpdate(string packagePath)
    {
        try
        {
            if (IsInstalled)
            {
                // 必须 UseShellExecute，否则不会弹 UAC 提权框，安装会直接失败
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(packagePath)
                {
                    UseShellExecute = true,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SP-",
                });

                return true;
            }

            return SwapPortable(packagePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 免安装版替换自己：正在运行的 exe 是锁着的，改不了。
    /// 交给一个后台 PowerShell 等本进程退出后再覆盖、重新启动。
    /// 用 -EncodedCommand 传脚本，避免中文路径在命令行里被转码搞坏。
    /// </summary>
    private static bool SwapPortable(string newExe)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current)) return false;

        static string Q(string path) => path.Replace("'", "''");

        var script = $$"""
            for ($i = 0; $i -lt 30; $i++) {
                try {
                    Move-Item -LiteralPath '{{Q(newExe)}}' -Destination '{{Q(current)}}' -Force -ErrorAction Stop
                    break
                } catch { Start-Sleep -Milliseconds 500 }
            }
            Start-Process -FilePath '{{Q(current)}}'
            """;

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
        });

        return true;
    }

    // ------------------------------------------------------------- 解析细节

    /// <summary>
    /// 把 tag 规整成 1.2.3 三段式：去掉 v 前缀，丢掉 -beta 之类的后缀。
    /// 规整不出来（比如 tag 是纯文字）返回 null，当作没有更新信息。
    /// </summary>
    internal static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var s = tag.Trim().TrimStart('v', 'V').Trim();

        // 只保留开头的「数字 + 点」段，1.2.3-beta.1 → 1.2.3
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].TrimEnd('.');

        if (!System.Version.TryParse(s, out var v)) return null;

        return $"{v.Major}.{Math.Max(v.Minor, 0)}.{Math.Max(v.Build, 0)}";
    }

    /// <summary>远端版本是否比当前版本新</summary>
    internal static bool IsNewerVersion(string remote, string current)
    {
        return System.Version.TryParse(remote, out var r)
            && System.Version.TryParse(current, out var c)
            && r > c;
    }

    /// <summary>
    /// 是否强制更新：Release 标题以「[强制]」开头，或 tag 以 -force 结尾。
    /// 约定写进 README，发版时照做即可。
    /// </summary>
    internal static bool IsForceRelease(string? name, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var t = name.Trim();
            if (t.StartsWith("[强制]", StringComparison.Ordinal)
                || t.StartsWith("【强制】", StringComparison.Ordinal))
                return true;
        }

        return !string.IsNullOrWhiteSpace(tag)
            && tag.TrimEnd().EndsWith("-force", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从附件列表里挑出安装包和免安装版的地址。
    /// 安装包找不到时回退到固定的「最新版」链接 —— 它不经过 API，即使超限也能下。
    /// </summary>
    private static (string Setup, string? Portable) PickDownloadUrls(JsonElement release)
    {
        var setup = (string?)null;
        var portable = (string?)null;

        if (release.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameEl)) continue;
                if (!asset.TryGetProperty("browser_download_url", out var urlEl)) continue;

                var assetName = nameEl.GetString();
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (string.Equals(assetName, SetupAsset, StringComparison.OrdinalIgnoreCase))
                    setup ??= url;
                else if (string.Equals(assetName, PortableAsset, StringComparison.OrdinalIgnoreCase))
                    portable ??= url;
            }
        }

        return (setup ?? LatestSetupUrl, portable);
    }

    /// <summary>
    /// 抹掉行内的 markdown 记号：链接只留文字，粗体星号和反引号直接去掉。
    /// </summary>
    private static string CleanInline(string text)
    {
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        text = text.Replace("**", "").Replace("`", "");
        return text.Trim();
    }
}
