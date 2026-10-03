using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windowsyikefu.Models;

namespace Windowsyikefu.Services;

/// <summary>
/// 客服系统 APP 协议客户端。
/// 签名规则：除 sign 外的所有 GET 查询参数按 ASCII 升序拼接 k=v（value 做 urlencode），
/// sign = hex(HMAC-SHA256(app_secret, 拼接串))。
/// </summary>
public sealed class KefuApiClient : IDisposable
{
    // AllowReadingFromString：服务端常把数值字段返回成字符串（如 "id":"246"），
    // System.Text.Json 默认会直接抛 JsonException，这里放宽以兼容这类响应。
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;

    /// <summary>当前生效的自定义请求头名字，改配置时要先把旧的摘掉</summary>
    private readonly List<string> _customHeaderNames = new();

    public string ApiUrl { get; }
    public string AppId { get; }
    public string AppSecret { get; }

    public KefuApiClient(string apiUrl, string appId, string appSecret, int timeoutSeconds = 40)
    {
        ApiUrl = NormalizeUrl(apiUrl);
        AppId = appId.Trim();
        AppSecret = appSecret.Trim();

        _http = new HttpClient(new HttpClientHandler
        {
            // 支持 gzip：省流量，也让请求更像浏览器
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // 复用连接，避免每次请求都重新握手（高频轮询下这点很重要）
            MaxConnectionsPerServer = 4,
        })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        // 请求头尽量贴近普通浏览器：
        // 国内的 CDN / WAF（安全狗、云锁、云 WAF 之类）对「非浏览器 UA + 缺 Accept / Referer」
        // 的程序化请求最敏感，很容易被当成爬虫直接拦掉，而 IP 又是变动的、没法靠白名单放行。
        // 这里保留自己的标识方便服务端统计，但整体伪装成浏览器请求的样子。
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            $"Chrome/122.0.0.0 Safari/537.36 WindowsYiKeFu/{UpdateService.CurrentVersion}");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");

        // Referer 指向接口所在站点：看起来就是从自己网站页面发出来的请求
        if (Uri.TryCreate(ApiUrl, UriKind.Absolute, out var baseUri))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", $"{baseUri.Scheme}://{baseUri.Authority}/");
    }

    /// <summary>
    /// 应用配置里自定义的请求头（每行一条 "名字: 值"，# 开头的行当注释忽略）。
    /// 走的是「暗号头」思路：客户端 IP 会变、没法在 CDN 上加 IP 白名单，
    /// 那就放一个别处猜不到的请求头，让 CDN / WAF 按「带这个头」直接放行。
    /// 可以在运行时反复调用（改设置后立即生效，不用重启），传空串等于清空。
    /// </summary>
    public void ApplyCustomHeaders(string? lines)
    {
        foreach (var name in _customHeaderNames)
        {
            try { _http.DefaultRequestHeaders.Remove(name); } catch { }
        }
        _customHeaderNames.Clear();

        if (string.IsNullOrWhiteSpace(lines)) return;

        foreach (var raw in lines.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var idx = line.IndexOf(':');
            if (idx <= 0) continue;

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0) continue;

            try
            {
                // 同名先移除：默认已经设过的（比如 User-Agent）也能被覆盖
                _http.DefaultRequestHeaders.Remove(name);

                if (_http.DefaultRequestHeaders.TryAddWithoutValidation(name, value))
                    _customHeaderNames.Add(name);
            }
            catch
            {
                // 头名字非法就跳过这一行，不影响其它配置
            }
        }
    }

    private static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return url;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url.TrimEnd('?', '&');
    }

    // ---------------------------------------------------------------- 签名

    internal string Sign(IDictionary<string, string> parameters)
    {
        var keys = parameters.Keys
            .Where(k => !string.Equals(k, "sign", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        foreach (var k in keys)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(k))
              .Append('=')
              .Append(Uri.EscapeDataString(parameters[k] ?? ""));
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecret));
        var raw = hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(raw).ToLowerInvariant();
    }

    private Dictionary<string, string> BuildSignedParams(string act, IEnumerable<KeyValuePair<string, string>>? extra)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["act"] = act,
            ["app_id"] = AppId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["nonce"] = Guid.NewGuid().ToString("N"),
        };

        if (extra != null)
        {
            foreach (var kv in extra) p[kv.Key] = kv.Value ?? "";
        }

        p["sign"] = Sign(p);
        return p;
    }

    private string BuildUrl(IDictionary<string, string> query)
    {
        var sb = new StringBuilder(ApiUrl);
        sb.Append(ApiUrl.Contains('?') ? '&' : '?');
        var first = true;
        foreach (var kv in query)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key))
              .Append('=')
              .Append(Uri.EscapeDataString(kv.Value ?? ""));
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 请求

    private async Task<ApiEnvelope<T>> GetAsync<T>(
        string act,
        IEnumerable<KeyValuePair<string, string>>? extra,
        CancellationToken ct)
    {
        var url = BuildUrl(BuildSignedParams(act, extra));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        return await ParseAsync<T>(resp, ct);
    }

    private async Task<ApiEnvelope<T>> PostFormAsync<T>(
        string act,
        IEnumerable<KeyValuePair<string, string>> form,
        CancellationToken ct)
    {
        var url = BuildUrl(BuildSignedParams(act, null));
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(url, content, ct);
        return await ParseAsync<T>(resp, ct);
    }

    private static async Task<ApiEnvelope<T>> ParseAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (string.IsNullOrWhiteSpace(body))
            throw new KefuApiException(-1, $"服务器返回空响应（HTTP {(int)resp.StatusCode}）");

        ApiEnvelope<T>? env = TryParse<T>(body);

        if (env == null)
        {
            var preview = body.Length > 200 ? body[..200] + "..." : body;
            throw new KefuApiException(-1,
                $"服务器返回的内容无法解析（HTTP {(int)resp.StatusCode}）：{preview}");
        }

        if (!env.Ok)
        {
            var msg = string.IsNullOrWhiteSpace(env.Msg)
                ? KefuApiException.Describe(env.Code)
                : env.Msg!;
            throw new KefuApiException(env.Code, msg);
        }

        return env;
    }

    /// <summary>
    /// 容错解析：先按标准 JSON 解析；失败则尝试修复「json 字符串里混入裸换行/制表符」
    /// 这类服务端拼串产生的不合法响应后重试。两次都失败才返回 null。
    /// </summary>
    private static ApiEnvelope<T>? TryParse<T>(string body)
    {
        try { return JsonSerializer.Deserialize<ApiEnvelope<T>>(body, JsonOpts); }
        catch (JsonException) { }

        try { return JsonSerializer.Deserialize<ApiEnvelope<T>>(EscapeRawControlChars(body), JsonOpts); }
        catch (JsonException) { return null; }
    }

    /// <summary>把 JSON 字符串字面量内部的裸控制字符转义，使其重新成为合法 JSON。</summary>
    private static string EscapeRawControlChars(string json)
    {
        var sb = new StringBuilder(json.Length + 32);
        var inString = false;
        var escaped = false;

        foreach (var ch in json)
        {
            if (inString)
            {
                if (escaped)
                {
                    sb.Append(ch);
                    escaped = false;
                    continue;
                }

                switch (ch)
                {
                    case '\\':
                        sb.Append(ch);
                        escaped = true;
                        continue;
                    case '"':
                        sb.Append(ch);
                        inString = false;
                        continue;
                    case '\n':
                        sb.Append("\\n");
                        continue;
                    case '\r':
                        sb.Append("\\r");
                        continue;
                    case '\t':
                        sb.Append("\\t");
                        continue;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                            continue;
                        }
                        sb.Append(ch);
                        continue;
                }
            }

            if (ch == '"') inString = true;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string S(long v) => v.ToString();

    // ---------------------------------------------------------------- 接口

    /// <summary>1. 连通测试</summary>
    public Task<ApiEnvelope<PingData>> PingAsync(CancellationToken ct = default)
        => GetAsync<PingData>("ping", null, ct);

    /// <summary>2. 绑定客服信息</summary>
    public Task<ApiEnvelope<KefuInfo>> KefuInfoAsync(CancellationToken ct = default)
        => GetAsync<KefuInfo>("kefuInfo", null, ct);

    /// <summary>3. 会话列表（status: 0=待接入 1=服务中 2=已结束，null=全部）</summary>
    public Task<ApiEnvelope<List<KefuSession>>> SessionListAsync(int? status = null, CancellationToken ct = default)
    {
        var extra = status.HasValue
            ? new[] { new KeyValuePair<string, string>("status", S(status.Value)) }
            : null;
        return GetAsync<List<KefuSession>>("sessionList", extra, ct);
    }

    /// <summary>4. 会话详情 + 消息（last_id=0 拉全量，否则拉增量）</summary>
    public Task<ApiEnvelope<SessionInfoData>> SessionInfoAsync(long sessionId, long lastId = 0, CancellationToken ct = default)
        => GetAsync<SessionInfoData>("sessionInfo", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
            new KeyValuePair<string, string>("last_id", S(lastId)),
        }, ct);

    /// <summary>
    /// 5. 发送消息。业务参数必须走 GET 才会被签名覆盖（POST body 不参与签名），
    /// 所以这里统一用 GET。msgType：text / image（图片 URL）/ order（订单 ID）。
    /// </summary>
    public Task<ApiEnvelope<SendResult>> SendMessageAsync(
        long sessionId,
        string content,
        string msgType = "text",
        CancellationToken ct = default)
        => GetAsync<SendResult>("sendMessage", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
            new KeyValuePair<string, string>("content", content),
            new KeyValuePair<string, string>("msg_type", msgType),
        }, ct);

    /// <summary>
    /// 上传图片：multipart 字段名固定为 file，服务端会强制转 WebP 并返回 URL。
    /// 限制：≤5MB，仅 jpg/png/gif/webp/bmp；同一客服 10 秒最多 1 张。
    /// </summary>
    public async Task<ApiEnvelope<UploadResult>> UploadImageAsync(string filePath, CancellationToken ct = default)
    {
        var url = BuildUrl(BuildSignedParams("uploadImage", null));

        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(filePath));
        form.Add(file, "file", Path.GetFileName(filePath));

        using var resp = await _http.PostAsync(url, form, ct);
        return await ParseAsync<UploadResult>(resp, ct);
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>6. 标记已读</summary>
    public Task<ApiEnvelope<JsonElement>> MarkReadAsync(long sessionId, CancellationToken ct = default)
        => GetAsync<JsonElement>("markRead", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
        }, ct);

    /// <summary>7. 结束会话</summary>
    public Task<ApiEnvelope<JsonElement>> CloseSessionAsync(long sessionId, CancellationToken ct = default)
        => PostFormAsync<JsonElement>("closeSession", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
        }, ct);

    /// <summary>8. 未读会话列表（轮询通知，推荐）</summary>
    public Task<ApiEnvelope<UnreadData>> UnreadListAsync(CancellationToken ct = default)
        => GetAsync<UnreadData>("unreadList", null, ct);

    /// <summary>9. 长轮询（服务端最长保持 25 秒）</summary>
    public Task<ApiEnvelope<PollData>> PollAsync(long sessionId, long lastId, CancellationToken ct = default)
        => GetAsync<PollData>("poll", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
            new KeyValuePair<string, string>("last_id", S(lastId)),
        }, ct);

    /// <summary>10. 客服在线心跳</summary>
    public Task<ApiEnvelope<JsonElement>> HeartbeatAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("heartbeat", null, ct);

    /// <summary>11. 用户详情（返回结构随服务端版本变化，按字段名宽松读取）</summary>
    public Task<ApiEnvelope<JsonElement>> UserInfoAsync(long sessionId, CancellationToken ct = default)
        => GetAsync<JsonElement>("userInfo", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
        }, ct);

    /// <summary>12. 重开会话</summary>
    public Task<ApiEnvelope<JsonElement>> ReopenSessionAsync(long sessionId, CancellationToken ct = default)
        => GetAsync<JsonElement>("reopenSession", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
        }, ct);

    /// <summary>13. 搜索订单（关键字可是订单 ID / 订单号 / 联系方式）</summary>
    public Task<ApiEnvelope<JsonElement>> OrderSearchAsync(string keyword, CancellationToken ct = default)
        => GetAsync<JsonElement>("orderSearch", new[]
        {
            new KeyValuePair<string, string>("kw", keyword),
        }, ct);

    /// <summary>14. 订单详情</summary>
    public Task<ApiEnvelope<JsonElement>> OrderInfoAsync(long orderId, CancellationToken ct = default)
        => GetAsync<JsonElement>("orderInfo", new[]
        {
            new KeyValuePair<string, string>("order_id", S(orderId)),
        }, ct);

    /// <summary>
    /// 15. 订单退款。money 传 null 表示按系统规则全额退款，否则需在 (0, 订单金额]。
    /// 需客服账号在后台开启订单处理权限。
    /// </summary>
    public Task<ApiEnvelope<JsonElement>> OrderRefundAsync(
        long orderId,
        decimal? money = null,
        string? remark = null,
        CancellationToken ct = default)
    {
        var extra = new List<KeyValuePair<string, string>>
        {
            new("order_id", S(orderId)),
            new("money", money.HasValue ? money.Value.ToString("0.00", CultureInfo.InvariantCulture) : ""),
        };

        if (!string.IsNullOrWhiteSpace(remark))
            extra.Add(new("tkbz", remark!));

        return GetAsync<JsonElement>("orderRefund", extra, ct);
    }

    /// <summary>16. 订单补单（需订单处理权限）</summary>
    public Task<ApiEnvelope<JsonElement>> OrderReorderAsync(long orderId, CancellationToken ct = default)
        => GetAsync<JsonElement>("orderReorder", new[]
        {
            new KeyValuePair<string, string>("order_id", S(orderId)),
        }, ct);

    /// <summary>17. 客服红包余额</summary>
    public Task<ApiEnvelope<JsonElement>> RedpackBalanceAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("redpackBalance", null, ct);

    /// <summary>18. 发红包给用户（0.01~5000，同会话 5 秒限 1 个）</summary>
    public Task<ApiEnvelope<JsonElement>> RedpackSendAsync(
        long sessionId, decimal money, CancellationToken ct = default)
        => GetAsync<JsonElement>("redpackSend", new[]
        {
            new KeyValuePair<string, string>("session_id", S(sessionId)),
            new KeyValuePair<string, string>("money", money.ToString("0.00", CultureInfo.InvariantCulture)),
        }, ct);

    /// <summary>19. 领取用户发来的红包</summary>
    public Task<ApiEnvelope<JsonElement>> RedpackReceiveAsync(long redpackId, CancellationToken ct = default)
        => GetAsync<JsonElement>("redpackReceive", new[]
        {
            new KeyValuePair<string, string>("redpack_id", S(redpackId)),
        }, ct);

    /// <summary>20. 红包详情</summary>
    public Task<ApiEnvelope<JsonElement>> RedpackInfoAsync(long redpackId, CancellationToken ct = default)
        => GetAsync<JsonElement>("redpackInfo", new[]
        {
            new KeyValuePair<string, string>("redpack_id", S(redpackId)),
        }, ct);

    /// <summary>21. 红包收发记录</summary>
    public Task<ApiEnvelope<JsonElement>> RedpackListAsync(int page = 1, CancellationToken ct = default)
        => GetAsync<JsonElement>("redpackList", new[]
        {
            new KeyValuePair<string, string>("page", S(page)),
        }, ct);

    /// <summary>22. 手动上/下线</summary>
    public Task<ApiEnvelope<JsonElement>> SetOnlineAsync(bool online, CancellationToken ct = default)
        => GetAsync<JsonElement>("setOnline", new[]
        {
            new KeyValuePair<string, string>("online", online ? "1" : "0"),
        }, ct);

    /// <summary>23. 退出登录（服务端置离线）</summary>
    public Task<ApiEnvelope<JsonElement>> LogoutAsync(CancellationToken ct = default)
        => GetAsync<JsonElement>("logout", null, ct);

    public void Dispose() => _http.Dispose();
}
