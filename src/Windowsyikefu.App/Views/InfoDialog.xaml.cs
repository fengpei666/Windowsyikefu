using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahApps.Metro.IconPacks;
using Windowsyikefu.Models;
using Windowsyikefu.Services;
using Windowsyikefu.ViewModels;

namespace Windowsyikefu.Views;

/// <summary>
/// 通用详情窗口：订单详情、红包详情、红包记录等
/// 都以「标题 + 若干键值行」的形式展示，服务端字段不完全固定，所以内容按返回值动态生成。
/// </summary>
public partial class InfoDialog : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与主窗口 / 设置窗口同一套材质：能开毛玻璃就用透明底，开不成退回不透明浅灰
        WindowEffects.ApplyBackdrop(this);
    }

    public InfoDialog(
        string title,
        IEnumerable<KeyValuePair<string, string>> rows,
        string? hint = null,
        PackIconLucideKind icon = PackIconLucideKind.Info)
    {
        InitializeComponent();

        TxtTitle.Text = title;
        IconTitle.Kind = icon;
        if (!string.IsNullOrWhiteSpace(hint)) TxtHint.Text = hint;

        var any = false;
        foreach (var row in rows)
        {
            Body.Children.Add(BuildRow(row.Key, row.Value));
            any = true;
        }

        if (!any)
        {
            Body.Children.Add(new TextBlock
            {
                Text = "没有可展示的字段",
                FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            });
        }
    }

    private UIElement BuildRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
        };

        var content = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(caption);
        grid.Children.Add(content);

        return grid;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------- 字段渲染

    /// <summary>服务端字段名 → 中文标签。没收录的字段直接用原名显示，不做隐藏。</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "ID",
        ["order_id"] = "订单ID",
        ["order_no"] = "订单号",
        ["orderno"] = "订单号",
        ["order_sn"] = "订单号",
        ["trade_no"] = "交易号",
        ["userid"] = "用户ID",
        ["user_id"] = "用户ID",
        ["username"] = "用户",
        ["nickname"] = "昵称",
        ["title"] = "标题",
        ["name"] = "名称",
        ["goods_name"] = "商品",
        ["money"] = "金额",
        ["price"] = "价格",
        ["amount"] = "金额",
        ["total"] = "合计",
        ["refund_price"] = "退款金额",
        ["refund"] = "已退款",
        ["status"] = "状态",
        ["status_text"] = "状态",
        ["state"] = "状态",
        ["remark"] = "备注",
        ["tkbz"] = "退款备注",
        ["content"] = "内容",
        ["addtime"] = "创建时间",
        ["add_time"] = "创建时间",
        ["createtime"] = "创建时间",
        ["paytime"] = "支付时间",
        ["pay_time"] = "支付时间",
        ["time"] = "时间",
        ["receive_time"] = "领取时间",
        ["regtime"] = "注册时间",
        ["mobile"] = "手机号",
        ["phone"] = "手机号",
        ["qq"] = "QQ",
        ["email"] = "邮箱",
        ["pay_type"] = "支付方式",
        ["paytype"] = "支付方式",
        ["session_id"] = "会话ID",
        ["from_kefu"] = "来源",
        ["balance"] = "余额",
        ["page"] = "页码",
        ["total_count"] = "总数",
    };

    /// <summary>把服务端返回的任意 JSON 拍平成键值行，数字时间戳自动转成本地时间。</summary>
    public static List<KeyValuePair<string, string>> FromJson(JsonElement data, int max = 40)
    {
        var rows = new List<KeyValuePair<string, string>>();

        switch (data.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in data.EnumerateObject())
                {
                    if (rows.Count >= max) break;
                    var value = FormatValue(p.Name, p.Value);
                    if (value == null) continue;      // 嵌套对象/数组跳过，避免刷屏
                    rows.Add(new KeyValuePair<string, string>(Label(p.Name), value));
                }
                break;

            case JsonValueKind.Array:
                var index = 1;
                foreach (var item in data.EnumerateArray())
                {
                    if (rows.Count >= max) break;

                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var summary = SummarizeItem(item);
                        rows.Add(new KeyValuePair<string, string>($"第 {index} 条", summary));
                    }
                    else
                    {
                        rows.Add(new KeyValuePair<string, string>($"第 {index} 条", JsonUtil.Str(item) ?? "—"));
                    }

                    index++;
                }
                break;

            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                break;

            default:
                rows.Add(new KeyValuePair<string, string>("内容", JsonUtil.Str(data) ?? "—"));
                break;
        }

        return rows;
    }

    /// <summary>列表里每条记录压成一行「订单号 · 金额 · 状态」式的摘要。</summary>
    private static string SummarizeItem(JsonElement item)
    {
        var parts = new List<string>();

        var no = JsonUtil.Pick(item, "order_no", "orderno", "order_id", "id", "trade_no", "redpack_id");
        if (!string.IsNullOrWhiteSpace(no)) parts.Add(no!);

        var money = JsonUtil.PickNumber(item, "money", "price", "amount", "total");
        if (money != 0m) parts.Add("¥" + money.ToString("0.00"));

        var status = JsonUtil.Pick(item, "status_text", "status_name", "state_text", "status");
        if (!string.IsNullOrWhiteSpace(status)) parts.Add(status!);

        var time = JsonUtil.PickNumber(item, "addtime", "add_time", "createtime", "time", "receive_time");
        if (time > 1_000_000_000m)
            parts.Add(TimeUtil.ToLocal((long)time).ToString("yyyy-MM-dd HH:mm"));

        return parts.Count > 0 ? string.Join("  ·  ", parts) : item.ToString();
    }

    private static string Label(string key) => Labels.TryGetValue(key, out var label) ? label : key;

    private static string? FormatValue(string key, JsonElement value)
    {
        // 嵌套结构不展开，保持列表清爽
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
            return null;

        if (value.ValueKind == JsonValueKind.Null) return "—";

        // 时间类字段：十位数字按 Unix 秒转本地时间
        if (key.Contains("time", StringComparison.OrdinalIgnoreCase))
        {
            var seconds = JsonUtil.Long(value);
            if (seconds > 1_000_000_000) return TimeUtil.ToLocal(seconds).ToString("yyyy-MM-dd HH:mm:ss");
        }

        return JsonUtil.Str(value) ?? "—";
    }
}
