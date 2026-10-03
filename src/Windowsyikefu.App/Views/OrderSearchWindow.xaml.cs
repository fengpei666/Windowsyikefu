using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Windowsyikefu.Models;
using Windowsyikefu.Services;
using Windowsyikefu.ViewModels;

namespace Windowsyikefu.Views;

/// <summary>
/// 搜索订单并作为订单卡片发送到当前会话（sendMessage &amp; msg_type=order）。
/// </summary>
public partial class OrderSearchWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与其它窗口统一：能开系统毛玻璃就用透明底，开不成退回不透明浅灰
        WindowEffects.ApplyBackdrop(this);
    }

    private readonly KefuApiClient _api;
    private readonly long _sessionId;

    public OrderSearchWindow(KefuApiClient api, long sessionId)
    {
        InitializeComponent();
        _api = api;
        _sessionId = sessionId;

        Loaded += (_, _) => TxtKeyword.Focus();
    }

    /// <summary>是否已成功发送（主窗口据此刷新消息）</summary>
    public bool Sent { get; private set; }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TxtKeyword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnSearch_Click(sender, e);
    }

    private async void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        var keyword = TxtKeyword.Text.Trim();
        if (keyword.Length == 0)
        {
            TxtStatus.Text = "请先填写搜索内容";
            return;
        }

        BtnSearch.IsEnabled = false;
        TxtStatus.Text = "搜索中…";

        try
        {
            var env = await _api.OrderSearchAsync(keyword);
            var rows = ExtractOrders(env.Data);

            ListResults.ItemsSource = rows;
            TxtStatus.Text = rows.Count == 0
                ? "没有找到匹配的订单"
                : $"找到 {rows.Count} 条，选中后点「发送到会话」";
        }
        catch (Exception ex)
        {
            ListResults.ItemsSource = null;
            TxtStatus.Text = ex.Message;
        }
        finally
        {
            BtnSearch.IsEnabled = true;
        }
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        if (ListResults.SelectedItem is not OrderRow row)
        {
            TxtHint.Text = "请先选中一条订单";
            return;
        }

        BtnSend.IsEnabled = false;
        TxtHint.Text = "发送中…";

        try
        {
            await _api.SendMessageAsync(_sessionId, row.Id.ToString(), "order");
            Sent = true;
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

    // ------------------------------------------------------------- 结果解析

    private static List<OrderRow> ExtractOrders(JsonElement data)
    {
        var rows = new List<OrderRow>();

        if (FindArray(data) is not { } array) return rows;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = PickLong(item, "order_id", "id", "oid");
            if (id == 0) continue;

            var no = JsonUtil.Pick(item, "order_no", "orderno", "order_sn", "trade_no");
            var title = JsonUtil.Pick(item, "title", "goods_name", "name", "desc");
            var status = JsonUtil.Pick(item, "status_text", "status_name", "state_text");
            var buyer = JsonUtil.Pick(item, "username", "nickname", "mobile", "phone", "user");
            var money = JsonUtil.PickNumber(item, "money", "price", "amount", "total", "pay_price");
            var time = (long)JsonUtil.PickNumber(item, "addtime", "add_time", "createtime", "time");

            var details = new List<string>();
            if (money != 0m) details.Add("¥" + money.ToString("0.00"));
            if (!string.IsNullOrWhiteSpace(status)) details.Add(status!);
            if (!string.IsNullOrWhiteSpace(buyer)) details.Add(buyer!);
            if (time > 1_000_000_000) details.Add(TimeUtil.ToLocal(time).ToString("yyyy-MM-dd HH:mm"));

            var head = !string.IsNullOrWhiteSpace(no) ? $"订单号 {no}" : $"订单 ID {id}";
            if (!string.IsNullOrWhiteSpace(title)) head += $"   {title}";

            rows.Add(new OrderRow
            {
                Id = id,
                Title = head,
                Detail = details.Count > 0 ? string.Join("  ·  ", details) : "—",
            });
        }

        return rows;
    }

    /// <summary>结果可能是裸数组，也可能包在 list/data/rows/orders 之类的字段里。</summary>
    private static JsonElement? FindArray(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array) return data;

        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "list", "data", "rows", "items", "orders", "result" })
            {
                if (JsonUtil.TryGetPropertyIgnoreCase(data, name, out var inner) &&
                    inner.ValueKind == JsonValueKind.Array)
                    return inner;
            }

            // 兜底：取第一个数组字段
            foreach (var p in data.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array) return p.Value;
            }
        }

        return null;
    }

    private static long PickLong(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!JsonUtil.TryGetPropertyIgnoreCase(obj, name, out var value)) continue;
            var parsed = JsonUtil.Long(value);
            if (parsed != 0) return parsed;
        }

        return 0;
    }

    private sealed class OrderRow
    {
        public long Id { get; init; }
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
    }
}
