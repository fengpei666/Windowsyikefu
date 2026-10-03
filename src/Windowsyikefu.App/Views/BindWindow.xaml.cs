using System.Windows;
using System.Windows.Media;
using Windowsyikefu.Services;

namespace Windowsyikefu.Views;

public partial class BindWindow : Window
{
    private bool _binding;
    private string _scheme = "";
    private readonly bool _append;

    public BindWindow(AppConfig config) : this(config, null) { }

    /// <param name="deepLink">从 easykefu:// 链接解析出的凭证，非空时预填并自动绑定</param>
    /// <param name="append">
    /// true = 追加站点模式（主界面顶部「+」按钮用）：只校验凭证并把结果交给调用方，
    /// 不覆盖当前配置，也不需要重启进程。
    /// </param>
    public BindWindow(AppConfig config, DeepLinkBindInfo? deepLink, bool append = false)
    {
        _append = append;
        InitializeComponent();

        // 追加站点时输入框留空：预填第一个站点的凭证会让人误以为已经填好了
        if (!_append)
        {
            TxtApiUrl.Text = config.ApiUrl;
            TxtAppId.Text = config.AppId;
            TxtAppSecret.Text = config.AppSecret;
        }

        Loaded += async (_, _) =>
        {
            if (deepLink != null)
            {
                ApplyDeepLink(deepLink);

                if (deepLink.HasCredentials)
                {
                    // 稍等一下让界面先画出来，再自动绑定，避免窗口还没显示就跳走
                    await Task.Delay(350);
                    await BindAsync();
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtApiUrl.Text)) TxtApiUrl.Focus();
            else if (string.IsNullOrWhiteSpace(TxtAppId.Text)) TxtAppId.Focus();
            else TxtAppSecret.Focus();
        };
    }

    /// <summary>追加站点模式下校验通过的凭证，由调用方写入配置</summary>
    public SiteConfig? ResultSite { get; private set; }

    /// <summary>用 Deep Link 中的凭证填充输入框，并把窗口提到最前</summary>
    public void ApplyDeepLink(DeepLinkBindInfo deepLink)
    {
        // api_url 可缺省，缺省时沿用已有配置（后台只传 app_id / app_secret 的情况）
        if (!string.IsNullOrWhiteSpace(deepLink.ApiUrl)) TxtApiUrl.Text = deepLink.ApiUrl;
        if (!string.IsNullOrWhiteSpace(deepLink.AppId)) TxtAppId.Text = deepLink.AppId;
        if (!string.IsNullOrWhiteSpace(deepLink.AppSecret)) TxtAppSecret.Text = deepLink.AppSecret;
        if (!string.IsNullOrWhiteSpace(deepLink.Scheme)) _scheme = deepLink.Scheme;

        WindowActivation.BringToFront(this);

        SetStatus(deepLink.HasCredentials
            ? "已接收桌面端绑定链接，正在自动绑定…"
            : "已接收绑定链接，请补全缺少的凭证后再绑定。", false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与主窗口 / 设置窗口统一：透明底 + XAML 里的半透明根 Border，让系统毛玻璃透出来
        WindowEffects.ApplyBackdrop(this);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnPaste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;

            if (!DeepLinkParser.TryParse(text, out var info))
            {
                SetStatus("剪贴板内容不是有效的绑定链接。", true);
                return;
            }

            if (!string.IsNullOrWhiteSpace(info.ApiUrl)) TxtApiUrl.Text = info.ApiUrl;
            if (!string.IsNullOrWhiteSpace(info.AppId)) TxtAppId.Text = info.AppId;
            if (!string.IsNullOrWhiteSpace(info.AppSecret)) TxtAppSecret.Text = info.AppSecret;

            SetStatus(info.HasCredentials
                ? "已从链接解析并填充凭证。"
                : "已解析链接，请补全缺少的凭证。", false);
        }
        catch (Exception ex)
        {
            SetStatus("读取剪贴板失败：" + ex.Message, true);
        }
    }

    private async void BtnBind_Click(object sender, RoutedEventArgs e) => await BindAsync();

    /// <summary>校验凭证并绑定；成功返回后由调用方决定是否关闭窗口</summary>
    private async Task BindAsync()
    {
        if (_binding) return;

        var api = TxtApiUrl.Text.Trim();
        var appId = TxtAppId.Text.Trim();
        var secret = TxtAppSecret.Text.Trim();

        if (api.Length == 0 || appId.Length == 0 || secret.Length == 0)
        {
            SetStatus("接口地址、APP ID、APP Secret 均为必填项。", true);
            return;
        }

        _binding = true;
        BtnBind.IsEnabled = false;
        BtnPaste.IsEnabled = false;
        SetStatus("正在连接服务器校验签名…", false);

        try
        {
            using var client = new KefuApiClient(api, appId, secret, 20);
            var ping = await client.PingAsync();

            var kefuName = ping.Data?.Kefu?.Name ?? ping.Data?.Kefu?.User ?? "未知客服";
            var appName = ping.Data?.App?.Name ?? "未知 APP";
            var online = ping.Data?.Kefu?.Online == 1 ? "在线" : "离线";

            SetStatus($"绑定成功：{appName} → 客服「{kefuName}」（{online}）。", false);

            // 绑定页靠"该时间点之后客户端调用过接口"判断已连接，这里补一次心跳，
            // 让页面立刻变绿显示「已连接 + IP」，不用等轮询循环。
            try { await client.HeartbeatAsync(); } catch { /* 心跳失败不影响绑定结果 */ }

            if (_append)
            {
                // 追加站点：把凭证交回主界面，由它加进标签列表并起一个新的服务实例
                ResultSite = new SiteConfig
                {
                    Name = appName is "未知 APP" ? "" : appName,
                    ApiUrl = api,
                    AppId = appId,
                    AppSecret = secret,
                };

                await Task.Delay(300);
                DialogResult = true;
                Close();
                return;
            }

            // 必须写进 sites：直接写 api_url / app_id / app_secret 的话，
            // Save() 里会用 sites[0] 把它们反写覆盖，刚绑好的新凭证会被悄悄丢掉。
            // 已绑过同一个站点就更新凭证，否则追加成一个新站点。
            var config = AppConfig.Load();

            var existing = config.Sites.FirstOrDefault(s =>
                SiteConfig.SameUrl(s.ApiUrl, api) && string.Equals(s.AppId, appId, StringComparison.Ordinal));

            if (existing != null)
            {
                existing.ApiUrl = api;
                existing.AppId = appId;
                existing.AppSecret = secret;
            }
            else
            {
                config.Sites.Add(new SiteConfig { ApiUrl = api, AppId = appId, AppSecret = secret });
            }

            if (!string.IsNullOrWhiteSpace(_scheme)) config.Scheme = _scheme;
            config.Save();

            // 稍微停一下让用户看到结果，再自动关闭
            await Task.Delay(500);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("绑定失败：" + ex.Message, true);
            _binding = false;
            BtnBind.IsEnabled = true;
            BtnPaste.IsEnabled = true;
        }
    }

    private void SetStatus(string text, bool isError)
    {
        TxtStatus.Text = text;
        TxtStatus.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))
            : (Brush)FindResource("TextSecondaryBrush");
    }
}
