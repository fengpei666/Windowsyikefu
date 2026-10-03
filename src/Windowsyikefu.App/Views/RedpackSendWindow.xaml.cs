using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Windowsyikefu.Models;
using Windowsyikefu.Services;

namespace Windowsyikefu.Views;

/// <summary>
/// 客服给用户发红包：先查余额，再调 redpackSend（0.01~5000，同会话 5 秒限 1 个）。
/// </summary>
public partial class RedpackSendWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与其它窗口统一：能开系统毛玻璃就用透明底，开不成退回不透明浅灰
        WindowEffects.ApplyBackdrop(this);
    }

    private readonly KefuApiClient _api;
    private readonly long _sessionId;

    public RedpackSendWindow(KefuApiClient api, long sessionId)
    {
        InitializeComponent();
        _api = api;
        _sessionId = sessionId;

        Loaded += async (_, _) =>
        {
            await LoadBalanceAsync();
            TxtMoney.Focus();
        };
    }

    /// <summary>是否已成功发出（主窗口据此刷新消息）</summary>
    public bool Sent { get; private set; }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TxtMoney_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSend_Click(sender, e);
    }

    private async Task LoadBalanceAsync()
    {
        try
        {
            var env = await _api.RedpackBalanceAsync();
            var balance = JsonUtil.PickNumber(env.Data, "balance", "money", "redpack_balance", "amount");
            TxtBalance.Text = "¥" + balance.ToString("0.00");
        }
        catch (Exception ex)
        {
            TxtBalance.Text = "获取失败";
            TxtHint.Text = ex.Message;
        }
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(TxtMoney.Text.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var money) || money <= 0)
        {
            TxtHint.Text = "请输入正确的金额";
            return;
        }

        if (money < 0.01m || money > 5000m)
        {
            TxtHint.Text = "单个红包金额需在 0.01 ~ 5000 元之间";
            return;
        }

        BtnSend.IsEnabled = false;
        TxtHint.Text = "发送中…";

        try
        {
            var env = await _api.RedpackSendAsync(_sessionId, money);
            var id = JsonUtil.PickNumber(env.Data, "id", "redpack_id");
            Sent = true;
            TxtHint.Text = "红包已发出" + (id > 0 ? $"（红包 ID {id:0}）" : "");

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            TxtHint.Text = ex.Message;
        }
        finally
        {
            BtnSend.IsEnabled = true;
        }
    }
}
