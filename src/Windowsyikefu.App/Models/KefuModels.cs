using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windowsyikefu.Models;

/// <summary>接口统一响应体：{ code, msg, data }</summary>
public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }

    public bool Ok => Code == 0 || Code == 200;
}

/// <summary>ping 返回的 data</summary>
public sealed class PingData
{
    [JsonPropertyName("server_time")] public long ServerTime { get; set; }
    [JsonPropertyName("app")] public AppInfo? App { get; set; }
    [JsonPropertyName("kefu")] public KefuInfo? Kefu { get; set; }
}

public sealed class AppInfo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class KefuInfo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("avatar")] public string? Avatar { get; set; }
    [JsonPropertyName("qq")] public string? Qq { get; set; }
    [JsonPropertyName("online")] public int Online { get; set; }
}

/// <summary>会话 cmy_kefu_session</summary>
public sealed class KefuSession
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("sid")] public string? Sid { get; set; }
    [JsonPropertyName("userid")] public long UserId { get; set; }
    [JsonPropertyName("username")] public string? UserName { get; set; }

    /// <summary>访客头像。服务端字段名是 user_avatar（不是 avatar），登录访客是完整 URL，游客为空串。</summary>
    [JsonPropertyName("user_avatar")] public string? UserAvatar { get; set; }

    /// <summary>访客当前是否在线。服务端返回的是 JSON 布尔，但个别版本可能给 0/1，宽容读取。</summary>
    [JsonPropertyName("user_online")] public JsonElement UserOnlineRaw { get; set; }
    public bool UserOnline => JsonUtil.Bool(UserOnlineRaw);

    /// <summary>访客最后活跃时间（秒级时间戳，0 表示未知）</summary>
    [JsonPropertyName("user_last_active")] public long UserLastActive { get; set; }

    [JsonPropertyName("kefu_id")] public long KefuId { get; set; }
    [JsonPropertyName("kefu_name")] public string? KefuName { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("last_msg")] public string? LastMsg { get; set; }
    [JsonPropertyName("last_time")] public long LastTime { get; set; }
    [JsonPropertyName("unread_kefu")] public int UnreadKefu { get; set; }
    [JsonPropertyName("unread_user")] public int UnreadUser { get; set; }
}

/// <summary>消息 cmy_kefu_message</summary>
public sealed class KefuMessage
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("session_id")] public long SessionId { get; set; }
    [JsonPropertyName("sender")] public int Sender { get; set; }   // 0=用户 1=客服 2=系统
    [JsonPropertyName("kefu_id")] public long KefuId { get; set; }
    [JsonPropertyName("msg_type")] public string? MsgType { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("isread")] public int IsRead { get; set; }
    [JsonPropertyName("addtime")] public long AddTime { get; set; }

    /// <summary>订单消息附带卡片数据（结构随服务端版本变化，按需取值）</summary>
    [JsonPropertyName("card")] public JsonElement Card { get; set; }

    /// <summary>红包消息状态：0 待领取 1 已领取 2 已退回</summary>
    [JsonPropertyName("redpack_status")] public JsonElement RedpackStatus { get; set; }

    public bool IsFromUser => Sender == 0;
    public bool IsSystem => Sender == 2;
}

/// <summary>sessionInfo 返回的 data</summary>
public sealed class SessionInfoData
{
    [JsonPropertyName("session")] public KefuSession? Session { get; set; }
    [JsonPropertyName("messages")] public List<KefuMessage>? Messages { get; set; }
}

/// <summary>poll 返回的 data</summary>
public sealed class PollData
{
    [JsonPropertyName("messages")] public List<KefuMessage>? Messages { get; set; }
    [JsonPropertyName("timeout")] public int Timeout { get; set; }
}

/// <summary>unreadList 返回的 data</summary>
public sealed class UnreadData
{
    [JsonPropertyName("unread_sessions")] public int UnreadSessions { get; set; }
    [JsonPropertyName("unread_total")] public int UnreadTotal { get; set; }
    [JsonPropertyName("sessions")] public List<KefuSession>? Sessions { get; set; }
}

/// <summary>sendMessage 返回的 data</summary>
public sealed class SendResult
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

/// <summary>uploadImage 返回的 data</summary>
public sealed class UploadResult
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>
/// 对 JsonElement 的宽容取值：服务端可能把数值返回成字符串、把布尔返回成 0/1，
/// 统一在这里消化，避免各处重复做类型判断。
/// </summary>
public static class JsonUtil
{
    public static string? Str(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.ToString(),
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        _ => null
    };

    public static long Long(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var s)) return s;
        return 0;
    }

    public static decimal Dec(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String && decimal.TryParse(e.GetString(), out var s)) return s;
        return 0m;
    }

    public static bool Bool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var n) && n != 0,
        JsonValueKind.String => e.GetString() is "1" or "true" or "True",
        _ => false
    };

    /// <summary>按候选字段名依次尝试，返回第一个非空值。</summary>
    public static string? Pick(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var v)) continue;
            var s = Str(v);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        return null;
    }

    /// <summary>按候选字段名依次尝试，返回第一个可解析为正数的值。</summary>
    public static decimal PickNumber(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return 0m;

        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var v)) continue;
            var d = Dec(v);
            if (d != 0m) return d;
        }

        return 0m;
    }

    /// <summary>
    /// 服务端返回结构不固定（data 下可能再套 order / info / detail 一层），
    /// 这里按候选字段名逐层下钻，返回第一个命中的对象；找不到就返回原对象。
    /// </summary>
    public static JsonElement Dig(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return root;

        var current = root;

        // 最多下钻 3 层，避免环形或异常结构死循环
        for (var depth = 0; depth < 3; depth++)
        {
            // 已经是订单对象了就别再往里钻
            if (LooksLikeOrder(current)) break;

            JsonElement next = default;
            var found = false;

            foreach (var name in names)
            {
                if (!TryGetPropertyIgnoreCase(current, name, out var inner)) continue;
                if (inner.ValueKind != JsonValueKind.Object) continue;

                next = inner;
                found = true;
                break;
            }

            if (!found) break;
            current = next;
        }

        return current;
    }

    /// <summary>粗略判断是不是一个订单对象：有订单号 / 标题 / 金额任一即算。</summary>
    private static bool LooksLikeOrder(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;

        foreach (var name in new[]
                 {
                     "order_no", "orderno", "order_sn", "trade_no", "order_id",
                     "title", "goods_name", "money", "pay_price", "addtime",
                 })
        {
            if (TryGetPropertyIgnoreCase(obj, name, out _)) return true;
        }

        return false;
    }

    /// <summary>大小写不敏感取属性（服务端字段命名不完全统一）。</summary>
    public static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
