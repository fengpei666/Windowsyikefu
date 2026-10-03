using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windowsyikefu.Services;

/// <summary>app_update.php 的响应体</summary>
public sealed class UpdateInfo
{
    /// <summary>0 已最新 / 1 强制更新 / 2 普通更新</summary>
    [JsonPropertyName("code")] public int Code { get; set; }

    [JsonPropertyName("has_update")] public bool HasUpdate { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("client_version")] public string? ClientVersion { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }
    [JsonPropertyName("update_log")] public string? UpdateLog { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("notice")] public UpdateNotice? Notice { get; set; }

    /// <summary>有更新且配了下载地址，才谈得上能更新</summary>
    public bool CanDownload => HasUpdate && !string.IsNullOrWhiteSpace(DownloadUrl);
}

public sealed class UpdateNotice
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

/// <summary>
/// 版本更新检查：请求云端的 app_update.php，把当前版本号带过去，
/// 由服务端判断有没有更新（下载地址也由服务端下发，客户端只负责打开）。
/// </summary>
public static class UpdateService
{
    /// <summary>在线更新服务（公开接口，不需要站点凭证）</summary>
    private const string Endpoint = "https://sq.zyycyun.cn/app_update.php";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>当前客户端版本，形如 1.0.0</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>拉一次更新信息；网络失败时抛出，由调用方决定怎么提示</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var url = $"{Endpoint}?version={Uri.EscapeDataString(CurrentVersion)}";
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize<UpdateInfo>(json, JsonOpts);
    }

    /// <summary>把更新说明 / 公告拼成对话框里的正文</summary>
    public static string BuildMessage(UpdateInfo info)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append("当前版本：").Append(CurrentVersion).AppendLine();
        sb.Append("最新版本：").Append(info.Version ?? "未知").AppendLine();

        if (!string.IsNullOrWhiteSpace(info.UpdateLog))
            sb.AppendLine().Append("更新内容：").AppendLine().Append(info.UpdateLog.Trim()).AppendLine();

        if (info.Notice is { Enabled: true } notice && !string.IsNullOrWhiteSpace(notice.Content))
        {
            sb.AppendLine()
              .Append("公告")
              .Append(string.IsNullOrWhiteSpace(notice.Title) ? "" : $"（{notice.Title.Trim()}）")
              .AppendLine()
              .Append(notice.Content.Trim())
              .AppendLine();
        }

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
}
