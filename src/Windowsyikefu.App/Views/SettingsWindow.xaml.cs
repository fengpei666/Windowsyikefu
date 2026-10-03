using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Windowsyikefu.Services;

namespace Windowsyikefu.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly List<KefuService> _services;
    private readonly ObservableCollection<string> _replies = new();

    private bool _pollReady;

    public SettingsWindow(AppConfig config, List<KefuService> services)
    {
        _config = config;
        _services = services;

        InitializeComponent();

        ChkToast.IsChecked = config.ToastEnabled;
        ChkSound.IsChecked = config.SoundEnabled;
        ChkCloseToTray.IsChecked = config.CloseToTray;
        ChkAutoStart.IsChecked = AutoStartManager.IsEnabled();
        ChkPaused.IsChecked = services.Count > 0 && services[0].Paused;
        ChkLowFrequency.IsChecked = config.LowFrequency;
        TxtCustomHeaders.Text = config.CustomHeaders;

        // 先赋值再开启回显，避免初始化时误触发
        SldPoll.Value = Math.Clamp(config.PollSeconds, 2, 30);
        TxtPollValue.Text = $"{(int)SldPoll.Value} 秒";
        _pollReady = true;

        foreach (var q in config.QuickReplies) _replies.Add(q);
        ListReplies.ItemsSource = _replies;
        UpdateReplyEmpty();

        TxtApiUrl.Text = string.IsNullOrWhiteSpace(config.ApiUrl) ? "（未设置）" : config.ApiUrl;
        TxtAppId.Text = string.IsNullOrWhiteSpace(config.AppId) ? "（未设置）" : config.AppId;

        TxtFooterHint.Text = "易客服 " +
            (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与主窗口同一套材质：开得成毛玻璃就用透明底透出系统材质，开不成退回不透明浅灰
        WindowEffects.ApplyBackdrop(this);
    }

    // ------------------------------------------------------------- 接收设置

    private void SldPoll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_pollReady || TxtPollValue == null) return;
        TxtPollValue.Text = $"{(int)Math.Round(e.NewValue)} 秒";
    }

    // ------------------------------------------------------------- 快捷回复

    private void TxtNewReply_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddReply();
    }

    private void BtnAddReply_Click(object sender, RoutedEventArgs e) => AddReply();

    private void AddReply()
    {
        var text = TxtNewReply.Text.Trim();
        if (text.Length == 0) return;

        if (!_replies.Contains(text)) _replies.Add(text);

        TxtNewReply.Clear();
        TxtNewReply.Focus();
        UpdateReplyEmpty();
    }

    private void BtnDeleteReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string text) return;

        _replies.Remove(text);
        UpdateReplyEmpty();
    }

    private void UpdateReplyEmpty()
        => TxtReplyEmpty.Visibility = _replies.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ------------------------------------------------------------- 账号

    private void BtnRebind_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "将清除所有已绑定站点并重新启动程序，稍后重新进入绑定向导。\n\n确定继续吗？",
            "更换绑定账号", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _config.Sites.Clear();
        _config.ApiUrl = "";
        _config.AppId = "";
        _config.AppSecret = "";
        _config.Save();

        App.Restart();
    }

    // ------------------------------------------------------------- 防 CDN 拦截

    /// <summary>
    /// 生成一个难猜的暗号并填进请求头框里。
    /// 用随机值是为了别人猜不到这个头，CDN 上按它放行才有效。
    /// </summary>
    private void BtnGenHeader_Click(object sender, RoutedEventArgs e)
    {
        TxtCustomHeaders.Text = "X-Kefu-Key: " + Guid.NewGuid().ToString("N")[..24];
    }

    // ------------------------------------------------------------- 检查更新

    private bool _checkingUpdate;

    /// <summary>
    /// 「检查更新」：请求云端的 app_update.php，有新版本就把更新说明 / 公告摆出来，
    /// 用户确认后打开服务端下发的下载地址。结果只写按钮旁边的小字，不额外弹提示。
    /// </summary>
    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;

        _checkingUpdate = true;
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateState.Text = "检查中…";

        try
        {
            var info = await UpdateService.CheckAsync();

            if (info == null || !info.CanDownload)
            {
                TxtUpdateState.Text = $"已是最新版本（{UpdateService.CurrentVersion}）";
                return;
            }

            TxtUpdateState.Text = $"发现新版本 {info.Version}";

            var result = MessageBox.Show(this,
                UpdateService.BuildMessage(info),
                info.Force ? $"请更新到 {info.Version}" : $"发现新版本 {info.Version}",
                info.Force ? MessageBoxButton.OK : MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (!info.Force && result != MessageBoxResult.OK) return;

            if (UpdateService.OpenDownload(info.DownloadUrl!))
            {
                TxtUpdateState.Text = "已打开下载页面";
            }
            else
            {
                TxtUpdateState.Text = "无法打开下载地址";
                MessageBox.Show(this, $"请手动下载：{info.DownloadUrl}", "下载新版本",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch
        {
            TxtUpdateState.Text = "检查失败，请检查网络";
        }
        finally
        {
            _checkingUpdate = false;
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------- 保存 / 取消

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _config.ToastEnabled = ChkToast.IsChecked == true;
        _config.SoundEnabled = ChkSound.IsChecked == true;
        _config.CloseToTray = ChkCloseToTray.IsChecked == true;
        _config.PollSeconds = (int)Math.Round(SldPoll.Value);
        _config.LowFrequency = ChkLowFrequency.IsChecked == true;
        _config.CustomHeaders = (TxtCustomHeaders.Text ?? "").Trim();
        _config.QuickReplies = _replies.ToList();
        _config.Save();

        // 开机自启写注册表
        AutoStartManager.Set(ChkAutoStart.IsChecked == true);

        // 暂停状态立即生效（所有站点一起）
        var paused = ChkPaused.IsChecked == true;
        foreach (var svc in _services) svc.Paused = paused;

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
