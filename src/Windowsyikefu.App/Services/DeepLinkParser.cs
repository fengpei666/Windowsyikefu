using System.Linq;

namespace Windowsyikefu.Services;

/// <summary>从 Deep Link 解析出的绑定信息</summary>
public sealed class DeepLinkBindInfo
{
    public string? ApiUrl { get; set; }
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }

    /// <summary>协议名，默认 easykefu，绑定页可用 ?scheme=xxx 覆盖</summary>
    public string Scheme { get; set; } = DeepLinkParser.DefaultScheme;

    /// <summary>页面是否要求自动拉起（auto=0 时为 false，但既然已经拉起，仅作记录）</summary>
    public bool Auto { get; set; } = true;

    /// <summary>未安装时的下载地址，仅作记录</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>原始链接</summary>
    public string RawUrl { get; set; } = "";

    /// <summary>三项凭证齐全</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiUrl) &&
        !string.IsNullOrWhiteSpace(AppId) &&
        !string.IsNullOrWhiteSpace(AppSecret);

    /// <summary>至少解析出一项</summary>
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(ApiUrl) ||
        !string.IsNullOrWhiteSpace(AppId) ||
        !string.IsNullOrWhiteSpace(AppSecret);
}

/// <summary>
/// Deep Link 解析：
/// 兼容 easykefu://bind?xxx、easykefu://bind/?xxx、easykefu://?xxx 三种形态，
/// 也兼容用户直接粘贴的纯 query 串（api_url=...&amp;app_id=...）。
/// </summary>
public static class DeepLinkParser
{
    public const string DefaultScheme = "easykefu";

    /// <summary>命令行里用来"仅激活已运行窗口"的内部标记</summary>
    public const string ArgShow = "--show";

    public static bool TryParse(string? raw, out DeepLinkBindInfo info)
    {
        info = new DeepLinkBindInfo();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // 系统把 URL 作为单个参数传入时可能带引号
        var text = raw.Trim().Trim('"').Trim();
        if (text.Length == 0) return false;
        info.RawUrl = text;

        string query;
        var isProtocolLink = false;
        var schemeIdx = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx > 0)
        {
            // 形如 <scheme>://<path>?<query>
            var scheme = text[..schemeIdx].Trim().ToLowerInvariant();
            if (IsValidScheme(scheme))
            {
                isProtocolLink = true;
                info.Scheme = scheme;
                query = text[(schemeIdx + 3)..];
            }
            else
            {
                // 前缀不是合法协议名（例如直接粘贴的 api_url=https://… 没做 URL 编码），
                // 这时不能当成协议链接丢掉，整串就是 query。
                query = text;
            }
        }
        else
        {
            // 不是协议链接：可能是直接粘贴的 query 串
            query = text;
        }

        // 取 query：优先 '?'，浏览器把参数放 fragment 时退化为 '#'
        var qIdx = query.IndexOf('?');
        if (qIdx >= 0)
        {
            query = query[(qIdx + 1)..];
        }
        else
        {
            var hIdx = query.IndexOf('#');
            if (hIdx >= 0) query = query[(hIdx + 1)..];
            else if (isProtocolLink) return false; // 协议链接但完全没有参数
        }

        // 去掉残留的 fragment（?a=1#b）
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];

        if (query.Length == 0) return false;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            string key, value;
            if (idx <= 0)
            {
                key = part.Trim().ToLowerInvariant();
                value = "";
            }
            else
            {
                key = part[..idx].Trim().ToLowerInvariant();
                value = Decode(part[(idx + 1)..].Trim());
            }

            switch (key)
            {
                case "api_url":
                case "apiurl":
                case "url": info.ApiUrl = value; break;

                case "app_id":
                case "appid": info.AppId = value; break;

                case "app_secret":
                case "appsecret":
                case "secret": info.AppSecret = value; break;

                case "scheme":
                    if (IsValidScheme(value.ToLowerInvariant())) info.Scheme = value.ToLowerInvariant();
                    break;

                case "download": info.DownloadUrl = value; break;

                case "auto": info.Auto = value != "0"; break;
            }
        }

        return info.HasAny;
    }

    /// <summary>从命令行参数中找出第一个 Deep Link / query 串</summary>
    public static string? FindInArgs(IEnumerable<string>? args)
    {
        if (args == null) return null;

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            var text = arg.Trim().Trim('"');

            // 跳过 --tray / --show 这类开关，以及 xxxx:... 之外的疑似文件路径
            if (text.StartsWith('-')) continue;

            var hasScheme = text.Contains("://", StringComparison.Ordinal);
            var hasKey = text.Contains('=') &&
                         (text.Contains("app_id=", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("app_secret=", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("api_url=", StringComparison.OrdinalIgnoreCase));

            if (hasScheme || hasKey) return text;
        }

        return null;
    }

    /// <summary>协议名合法性：字母开头，仅允许字母、数字、+ - .</summary>
    public static bool IsValidScheme(string scheme)
    {
        if (string.IsNullOrWhiteSpace(scheme) || scheme.Length > 40) return false;
        if (!char.IsLetter(scheme[0])) return false;
        return scheme.All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    private static string Decode(string value)
    {
        if (value.Length == 0) return value;
        try { return Uri.UnescapeDataString(value); }
        catch { return value; } // 含非法 % 序列时按原文保留
    }
}
