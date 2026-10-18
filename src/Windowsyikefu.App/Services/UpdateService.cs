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

    /// <summary>安装包下载地址；实在找不到资产时回退到 Release 页面</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>是否强制更新（Release 标题带 [强制] 前缀，或 tag 以 -force 结尾）</summary>
    public bool Force { get; set; }

    /// <summary>更新说明，取自 Release 正文</summary>
    public string? UpdateLog { get; set; }

    /// <summary>有更新、且拿得到下载地址，才谈得上能更新</summary>
    public bool CanDownload => HasUpdate && !string.IsNullOrWhiteSpace(DownloadUrl);
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

    /// <summary>更新说明太长会把对话框撑爆，超过这个长度截断</summary>
    private const int MaxLogLength = 800;

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

        var version = NormalizeTag(tag);
        if (version == null) return null;

        var hasUpdate = IsNewerVersion(version, CurrentVersion);

        return new UpdateInfo
        {
            HasUpdate = hasUpdate,
            Version = version,
            Force = hasUpdate && IsForceRelease(name, tag),
            UpdateLog = CleanLog(body),
            DownloadUrl = hasUpdate ? PickDownloadUrl(root) : null,
        };
    }

    /// <summary>把更新说明拼成对话框里的正文</summary>
    public static string BuildMessage(UpdateInfo info)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append("当前版本：").Append(CurrentVersion).AppendLine();
        sb.Append("最新版本：").Append(info.Version ?? "未知").AppendLine();

        if (!string.IsNullOrWhiteSpace(info.UpdateLog))
            sb.AppendLine().Append("更新内容：").AppendLine().Append(info.UpdateLog).AppendLine();

        if (info.Force)
            sb.AppendLine().Append("本次为强制更新：下载安装后请重新打开本程序。");

        return sb.ToString().TrimEnd();
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
    /// 下载地址优先级：安装包 → 免安装版 → 固定最新版链接 → Release 页面。
    /// 前两个来自本次发布的附件列表，后两个是不依赖 API 的兜底。
    /// </summary>
    private static string PickDownloadUrl(JsonElement release)
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

        if (setup != null) return setup;
        if (portable != null) return portable;

        // 附件列表读不到（或没传安装包）时，用固定的「最新版」下载地址，它不经过 API
        return LatestSetupUrl;
    }

    /// <summary>
    /// Release 正文当更新说明用：去掉 markdown 的标题井号和粗体星号，
    /// 避免在纯文本对话框里显示一堆符号。
    /// </summary>
    private static string? CleanLog(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var text = body.Replace("\r\n", "\n").Trim();

        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = text.Replace("**", "");

        if (text.Length > MaxLogLength)
            text = text[..MaxLogLength].TrimEnd() + "…";

        return text.Trim();
    }
}
