using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windowsyikefu.Services;

/// <summary>
/// 一个站点的绑定凭证。一个客服可以绑定多个网站，每个网站一份凭证，
/// 对应主界面顶部的一个标签页。
/// </summary>
public sealed class SiteConfig
{
    /// <summary>标签上显示的名字，绑定成功后用服务端的 app 名自动填充</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("api_url")] public string ApiUrl { get; set; } = "";
    [JsonPropertyName("app_id")] public string AppId { get; set; } = "";
    [JsonPropertyName("app_secret")] public string AppSecret { get; set; } = "";

    [JsonIgnore]
    public bool IsBound =>
        !string.IsNullOrWhiteSpace(ApiUrl) &&
        !string.IsNullOrWhiteSpace(AppId) &&
        !string.IsNullOrWhiteSpace(AppSecret);

    /// <summary>接口地址的域名，站点名缺失时拿它当标签名</summary>
    [JsonIgnore]
    public string Host
    {
        get
        {
            try
            {
                var uri = new Uri(ApiUrl);
                return string.IsNullOrWhiteSpace(uri.Host) ? ApiUrl : uri.Host;
            }
            catch
            {
                return ApiUrl;
            }
        }
    }

    /// <summary>标签显示名：优先自定义名字，其次域名</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>
    /// 站点唯一标识。通知里带的是它：站点增删会让下标变化，
    /// 用它才能保证点通知时找对的还是原来那个站点。
    /// </summary>
    [JsonIgnore]
    public string Key => $"{ApiUrl}|{AppId}";

    /// <summary>
    /// 两个接口地址是否指向同一个站点。
    /// 统一在这里比较（忽略大小写与末尾斜杠），避免「有没有绑过」在各处的判断不一致。
    /// </summary>
    public static bool SameUrl(string? a, string? b)
        => string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>本地配置，保存在 %AppData%\Windowsyikefu\config.json</summary>
public sealed class AppConfig
{
    /// <summary>
    /// 已绑定的站点列表。顶部标签页的个数就是这里的个数：
    /// 绑一个显示一个，绑多个显示多个。
    /// </summary>
    [JsonPropertyName("sites")] public List<SiteConfig> Sites { get; set; } = new();

    // ↓ 下面三个是旧版单站点字段。保留它们只是为了让老配置文件能平滑迁移，
    //   运行时一律以 Sites 为准，读写时会自动与 Sites[0] 保持一致。
    [JsonPropertyName("api_url")] public string ApiUrl { get; set; } = "";
    [JsonPropertyName("app_id")] public string AppId { get; set; } = "";
    [JsonPropertyName("app_secret")] public string AppSecret { get; set; } = "";

    /// <summary>Deep Link 协议名，默认 easykefu（绑定页可用 ?scheme=xxx 覆盖）</summary>
    [JsonPropertyName("scheme")] public string Scheme { get; set; } = "easykefu";

    /// <summary>未读轮询间隔（秒）</summary>
    [JsonPropertyName("poll_seconds")] public int PollSeconds { get; set; } = 3;

    /// <summary>
    /// 低频轮询模式：把各处轮询间隔整体拉长（未读 ≥8 秒、消息 5 秒、列表 15 秒）。
    /// 客户端 IP 不固定、没法加白名单时，降低请求频率是避免被 CDN / WAF 拉黑最有效的办法。
    /// </summary>
    [JsonPropertyName("low_frequency")] public bool LowFrequency { get; set; }

    /// <summary>关闭窗口时最小化到托盘而非退出</summary>
    [JsonPropertyName("close_to_tray")] public bool CloseToTray { get; set; } = true;

    /// <summary>是否弹出 Windows 原生通知</summary>
    [JsonPropertyName("toast_enabled")] public bool ToastEnabled { get; set; } = true;

    /// <summary>是否播放提示音</summary>
    [JsonPropertyName("sound_enabled")] public bool SoundEnabled { get; set; } = true;

    /// <summary>
    /// 自定义请求头，每行一条 "名字: 值"（例如 X-Kefu-Key: abc123）。
    /// 客户端 IP 是浮动的、没法在 CDN 上加 IP 白名单，用这个「暗号头」最省事：
    /// CDN / WAF 里配一条「请求头 X-Kefu-Key 等于 abc123 → 跳过防护」即可。
    /// </summary>
    [JsonPropertyName("custom_headers")] public string CustomHeaders { get; set; } = "";

    /// <summary>全局快捷回复短语</summary>
    [JsonPropertyName("quick_replies")] public List<string> QuickReplies { get; set; } = new()
    {
        "您好，请问有什么可以帮您？",
        "稍等，我马上为您处理。",
        "好的，感谢您的耐心等待。",
        "还有什么可以帮到您吗？"
    };

    [JsonIgnore] public bool IsBound => Sites.Any(s => s.IsBound);

    // ---------- 持久化 ----------

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Windowsyikefu");

    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// 老配置只有一份凭证（api_url / app_id / app_secret），这里把它迁移成唯一的站点，
    /// 升级后用户原来的绑定照常可用；顺带丢掉不完整的站点。
    /// </summary>
    private void Normalize()
    {
        Sites ??= new List<SiteConfig>();
        Sites.RemoveAll(s => !s.IsBound);

        var legacy = new SiteConfig { ApiUrl = ApiUrl, AppId = AppId, AppSecret = AppSecret };
        if (Sites.Count == 0 && legacy.IsBound) Sites.Add(legacy);

        SyncLegacyFields();
    }

    /// <summary>旧字段始终等于第一个站点，保证老版本代码 / 外部脚本读到的值仍然正确</summary>
    private void SyncLegacyFields()
    {
        if (Sites.Count == 0) return;

        ApiUrl = Sites[0].ApiUrl;
        AppId = Sites[0].AppId;
        AppSecret = Sites[0].AppSecret;
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* 配置损坏时回退到默认值 */ }

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            SyncLegacyFields();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 忽略写入失败，不影响运行 */ }
    }
}
