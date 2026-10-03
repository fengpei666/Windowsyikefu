using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windowsyikefu.Models;

namespace Windowsyikefu.ViewModels;

public static class TimeUtil
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime ToLocal(long unixSeconds)
        => unixSeconds <= 0 ? DateTime.MinValue : Epoch.AddSeconds(unixSeconds).ToLocalTime();

    /// <summary>列表用：今天显示 HH:mm，今年显示 MM-dd，更早显示 yyyy-MM-dd</summary>
    public static string ToListText(long unixSeconds)
    {
        var t = ToLocal(unixSeconds);
        if (t == DateTime.MinValue) return "";
        var now = DateTime.Now;
        if (t.Date == now.Date) return t.ToString("HH:mm");
        if (t.Year == now.Year) return t.ToString("MM-dd");
        return t.ToString("yyyy-MM-dd");
    }

    /// <summary>消息气泡用</summary>
    public static string ToMessageText(long unixSeconds)
    {
        var t = ToLocal(unixSeconds);
        if (t == DateTime.MinValue) return "";
        var now = DateTime.Now;
        if (t.Date == now.Date) return t.ToString("HH:mm");
        if (t.Year == now.Year) return t.ToString("MM-dd HH:mm");
        return t.ToString("yyyy-MM-dd HH:mm");
    }
}

/// <summary>会话列表项</summary>
public sealed class SessionItem : INotifyPropertyChanged
{
    public long Id { get; }

    private string _title = "";
    private string _lastMsg = "";
    private string _timeText = "";
    private int _unread;
    private int _status;
    private ImageSource? _avatar;
    private bool _userOnline;
    private long _userLastActive;

    public SessionItem(KefuSession s)
    {
        Id = s.Id;
        Update(s, updateAvatar: true);
    }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string LastMsg
    {
        get => _lastMsg;
        private set => Set(ref _lastMsg, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => Set(ref _timeText, value);
    }

    public int Unread
    {
        get => _unread;
        private set
        {
            if (!Set(ref _unread, value)) return;
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadText));
        }
    }

    public bool HasUnread => _unread > 0;

    /// <summary>未读角标上的文字：超过 99 显示 99+，免得角标被撑得很长</summary>
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString();

    /// <summary>进入会话后清除本地未读角标</summary>
    public void ClearUnread() => Unread = 0;

    public int Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanReopen));
        }
    }

    public string StatusText => _status switch
    {
        0 => "待接入",
        1 => "服务中",
        2 => "已结束",
        _ => ""
    };

    /// <summary>只有已结束的会话才谈得上「重新打开」，右键菜单据此灰显</summary>
    public bool CanReopen => _status == 2;

    /// <summary>最后消息时间（用于列表排序）</summary>
    public long LastTime { get; private set; }

    public string AvatarText => string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1];

    /// <summary>访客真实头像；为 null 时界面回退到昵称首字</summary>
    public ImageSource? Avatar
    {
        get => _avatar;
        private set
        {
            if (!Set(ref _avatar, value)) return;
            OnPropertyChanged(nameof(HasAvatar));
            OnPropertyChanged(nameof(ShowAvatarText));
        }
    }

    /// <summary>头像原始地址（详情面板切会话时直接取用，避免重复解析）</summary>
    public string? AvatarUrl { get; private set; }

    public bool HasAvatar => _avatar != null;

    /// <summary>没有头像图时用首字占位</summary>
    public bool ShowAvatarText => _avatar == null;

    /// <summary>访客当前是否在线（服务端心跳，访客切走/关闭页面后自动变灰）</summary>
    public bool IsUserOnline
    {
        get => _userOnline;
        private set
        {
            if (Set(ref _userOnline, value)) OnPropertyChanged(nameof(VisitorStateText));
        }
    }

    /// <summary>访客最后活跃时间（秒级时间戳，0 表示未知）</summary>
    public long UserLastActive
    {
        get => _userLastActive;
        private set
        {
            if (Set(ref _userLastActive, value)) OnPropertyChanged(nameof(VisitorStateText));
        }
    }

    /// <summary>访客在线状态文案，详情面板直接显示这一行</summary>
    public string VisitorStateText
    {
        get
        {
            if (_userOnline) return "在线";
            return _userLastActive > 0
                ? $"离线 · 最后活跃 {TimeUtil.ToMessageText(_userLastActive)}"
                : "离线";
        }
    }

    /// <summary>
    /// 刷新会话的基础字段。
    /// updateAvatar 只有「会话列表」这一路数据（进应用 / 6 秒定时 / 手动点刷新）才传 true：
    /// unreadList、sessionInfo 这两路返回的 user_avatar 并不稳定，跟着它们改头像会乱跳，
    /// 也会反复触发下载把界面拖卡。
    /// </summary>
    public void Update(KefuSession s, bool updateAvatar = false)
    {
        Title = string.IsNullOrWhiteSpace(s.UserName) ? $"访客 {s.Id}" : s.UserName!.Trim();
        LastMsg = (s.LastMsg ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        TimeText = TimeUtil.ToListText(s.LastTime);
        Unread = s.UnreadKefu;
        Status = s.Status;
        LastTime = s.LastTime;
        UserLastActive = s.UserLastActive;
        IsUserOnline = s.UserOnline;

        if (updateAvatar) UpdateAvatar(s.UserAvatar);

        OnPropertyChanged(nameof(AvatarText));
    }

    /// <summary>
    /// 头像单独一条链路：只在拿到可靠的 user_avatar（会话列表）时走。
    /// 地址没变就完全不动；地址为空则沿用上一次的，绝不置空。
    /// </summary>
    private void UpdateAvatar(string? rawUrl)
    {
        var avatarUrl = string.IsNullOrWhiteSpace(rawUrl) ? AvatarUrl : rawUrl;
        var changed = !string.Equals(AvatarUrl, avatarUrl, StringComparison.Ordinal);
        AvatarUrl = avatarUrl;

        if (!changed && _avatar != null) return;

        var url = avatarUrl;
        // 后台解完码再补图：既不会闪出上一位访客的头像，也不会长时间空着
        var img = ImageUtil.Load(url, ready =>
        {
            if (string.Equals(AvatarUrl, url, StringComparison.Ordinal)) Avatar = ready;
        });

        if (img != null) Avatar = img;         // 命中缓存，立即换上
        else if (changed) Avatar = null;       // 地址真的变了才退回占位，等回调补图
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>消息种类：决定气泡里渲染文本、图片还是卡片</summary>
public enum MessageKind
{
    Text,
    Image,
    Order,
    Redpack,
}

/// <summary>聊天气泡项</summary>
public sealed class MessageItem : INotifyPropertyChanged
{
    /// <summary>相对图片路径（/upload/xxx.webp）解析用的站点根地址，由主窗口按配置写入</summary>
    public static string ImageBaseUrl
    {
        get => ImageUtil.BaseUrl;
        set => ImageUtil.BaseUrl = value;
    }

    public long Id { get; }
    public string Content { get; }
    public string TimeText { get; }
    public bool IsMine { get; }      // 客服（自己）发出
    public bool IsSystem { get; }
    public MessageKind Kind { get; }

    /// <summary>图片消息：图片地址与已加载的位图（后台解完码会再通知一次）</summary>
    public string? ImageUrl { get; }

    private ImageSource? _image;
    public ImageSource? Image
    {
        get => _image;
        private set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            OnPropertyChanged(nameof(Image));
        }
    }

    /// <summary>补全成绝对地址，供「用系统看图程序打开」使用</summary>
    public string? AbsoluteImageUrl => string.IsNullOrWhiteSpace(ImageUrl)
        ? null
        : BuildUri(ImageUrl!).ToString();

    /// <summary>
    /// 气泡旁边的头像：访客消息用当前访客的头像，自己的消息用客服头像。
    /// 由主窗口统一贴（见 SyncMessageAvatars），跟着会话切换走。
    /// </summary>
    private ImageSource? _avatar;

    public ImageSource? Avatar
    {
        get => _avatar;
        set
        {
            if (ReferenceEquals(_avatar, value)) return;
            _avatar = value;
            OnPropertyChanged(nameof(Avatar));
            OnPropertyChanged(nameof(HasAvatar));
            OnPropertyChanged(nameof(ShowAvatarText));
        }
    }

    public bool HasAvatar => _avatar != null;

    /// <summary>拿不到头像图时退回首字占位</summary>
    public bool ShowAvatarText => _avatar == null;

    /// <summary>占位字：自己的消息是「我」，访客的是「客」</summary>
    public string AvatarText => IsMine ? "我" : "客";

    /// <summary>订单卡片</summary>
    public long OrderId { get; }

    /// <summary>卡片上展示的字段。服务端没给的一律用 — 占位，避免卡片出现空行</summary>
    public string OrderNo => Blank(_orderNo);
    public string OrderTitle => Blank(_orderTitle);
    public string OrderAmount => Blank(_orderAmount);
    public string OrderStatus => Blank(_orderStatus);
    public string OrderTime => Blank(_orderTime);
    public string OrderBuyer => Blank(_orderBuyer);

    /// <summary>买家信息为空时整行隐藏</summary>
    public bool HasOrderBuyer => !string.IsNullOrWhiteSpace(_orderBuyer);

    /// <summary>消息里只有订单 ID、拿不到订单号等明细时，需要去 orderInfo 补一次</summary>
    public bool NeedsOrderDetail => OrderId != 0 &&
        string.IsNullOrWhiteSpace(_orderNo) &&
        string.IsNullOrWhiteSpace(_orderTitle) &&
        string.IsNullOrWhiteSpace(_orderAmount);

    private static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s!;

    /// <summary>红包卡片</summary>
    public long RedpackId { get; }
    public int RedpackStatus => _redpackStatus;

    private bool _isRead;

    public MessageItem(KefuMessage m)
    {
        Id = m.Id;
        Content = m.Content ?? "";
        TimeText = TimeUtil.ToMessageText(m.AddTime);
        IsMine = m.Sender == 1;
        IsSystem = m.Sender == 2;
        _isRead = m.IsRead == 1;

        var type = (m.MsgType ?? "").Trim().ToLowerInvariant();

        // 红包消息由 redpackSend 自动生成，content 形如 "redpack:66"
        if (TryParsePrefixedId(Content, "redpack:", out var redpackId))
        {
            Kind = MessageKind.Redpack;
            RedpackId = redpackId;
            _redpackStatus = (int)JsonUtil.Long(m.RedpackStatus);
        }
        else if (type == "image")
        {
            Kind = MessageKind.Image;
            ImageUrl = Content.Trim();
            Image = LoadImage(ImageUrl, img => Image = img);
        }
        else if (type == "order")
        {
            Kind = MessageKind.Order;

            // content 是 "order:订单ID"；个别版本可能直接给数字
            if (TryParsePrefixedId(Content, "order:", out var orderId))
                OrderId = orderId;
            else
                OrderId = long.TryParse(Content.Trim(), out var raw) ? raw : 0;

            FillOrderCard(m.Card);
        }
        else
        {
            Kind = MessageKind.Text;
        }
    }

    private void FillOrderCard(System.Text.Json.JsonElement card)
    {
        if (card.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        _orderNo = JsonUtil.Pick(card, "order_no", "orderno", "order_sn", "sn", "trade_no") ?? "";
        _orderTitle = JsonUtil.Pick(card, "title", "name", "goods_name", "goods", "desc", "remark") ?? "";
        _orderStatus = JsonUtil.Pick(card, "status_text", "status_name", "state_text", "status_str") ?? "";
        _orderBuyer = JsonUtil.Pick(card, "username", "nickname", "mobile", "phone", "user", "buyer") ?? "";
        _orderTime = FormatOrderTime(JsonUtil.PickNumber(card, "addtime", "add_time", "createtime", "pay_time", "time"));

        var money = JsonUtil.PickNumber(card, "money", "price", "amount", "total", "pay_price");
        _orderAmount = money > 0 ? "¥" + money.ToString("0.00") : "";
    }

    /// <summary>
    /// 消息里没带卡片数据（服务端只回了 order:ID）时，用 orderInfo 的结果把卡片补全。
    /// 返回是否真的补到了内容，没补到就不用再重复请求。
    /// </summary>
    public bool ApplyOrderInfo(System.Text.Json.JsonElement data)
    {
        var node = JsonUtil.Dig(data, "order", "data", "info", "detail", "result");
        if (node.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

        var before = _orderNo + _orderTitle + _orderAmount + _orderStatus;
        FillOrderCard(node);

        if (_orderNo + _orderTitle + _orderAmount + _orderStatus == before) return false;

        OnPropertyChanged(nameof(OrderNo));
        OnPropertyChanged(nameof(OrderTitle));
        OnPropertyChanged(nameof(OrderAmount));
        OnPropertyChanged(nameof(OrderStatus));
        OnPropertyChanged(nameof(OrderTime));
        OnPropertyChanged(nameof(OrderBuyer));
        OnPropertyChanged(nameof(HasOrderBuyer));
        OnPropertyChanged(nameof(NeedsOrderDetail));
        return true;
    }

    /// <summary>时间戳可能是秒也可能是毫秒，太离谱的直接不显示</summary>
    private static string FormatOrderTime(decimal raw)
    {
        var value = (long)raw;
        if (value <= 0) return "";

        // 毫秒级时间戳先降到秒
        while (value > 99_999_999_999) value /= 1000;
        if (value < 1_000_000_000) return "";

        return TimeUtil.ToLocal(value).ToString("yyyy-MM-dd HH:mm");
    }

    private string _orderNo = "";
    private string _orderTitle = "";
    private string _orderStatus = "";
    private string _orderAmount = "";
    private string _orderTime = "";
    private string _orderBuyer = "";

    private static bool TryParsePrefixedId(string content, string prefix, out long id)
    {
        id = 0;
        if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return long.TryParse(content[prefix.Length..].Trim(), out id);
    }

    private static ImageSource? LoadImage(string? url, Action<ImageSource>? onReady = null)
        => ImageUtil.Load(url, onReady);

    private static Uri BuildUri(string url) => ImageUtil.BuildUri(url);

    public bool IsBubble => !IsSystem;

    /// <summary>气泡正文颜色：自己发的是绿底白字，访客消息是白底深字</summary>
    public Brush BubbleForeground => IsMine ? Brushes.White : PrimaryBrush;

    /// <summary>气泡次要文字（时间、送达状态）颜色</summary>
    public Brush BubbleMutedForeground => IsMine ? MutedOnBrand : SecondaryBrush;

    private static readonly Brush MutedOnBrand = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));

    private static Brush? _primaryBrush;
    private static Brush? _secondaryBrush;

    private static Brush PrimaryBrush => _primaryBrush ??=
        Application.Current?.TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;

    private static Brush SecondaryBrush => _secondaryBrush ??=
        Application.Current?.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

    /// <summary>红包状态文案（0 待领取 / 1 已领取 / 2 已退回）</summary>
    public string RedpackStatusText => _redpackStatus switch
    {
        1 => "已领取",
        2 => "已退回",
        _ => "待领取",
    };

    /// <summary>待领取的访客红包才显示「领取」按钮</summary>
    public bool CanReceiveRedpack => _redpackStatus == 0 && !IsMine;

    // ---------------------------------------------------------------- 右键菜单

    /// <summary>可按类型复制 / 打开的判定，直接绑到菜单项的 Visibility 上</summary>
    public bool CanCopyText => Kind == MessageKind.Text && !string.IsNullOrWhiteSpace(Content);

    public bool CanCopyImage => Kind == MessageKind.Image && !string.IsNullOrWhiteSpace(ImageUrl);

    /// <summary>有订单号或订单 ID 才谈得上复制</summary>
    public bool CanCopyOrderNo => Kind == MessageKind.Order && OrderId != 0;

    /// <summary>订单卡片复制出去的内容：有订单号用订单号，没有就退回订单 ID</summary>
    public string CopyableOrderNo =>
        !string.IsNullOrWhiteSpace(_orderNo) ? _orderNo!.Trim() : $"#{OrderId}";

    /// <summary>红包是服务端按规则生成的消息，内容没法原样重发</summary>
    public bool CanForward => Kind != MessageKind.Redpack;

    /// <summary>转发时原样重发的内容：文本发原文，图片发图片地址，订单发 order:ID</summary>
    public string ForwardContent => Kind switch
    {
        MessageKind.Image => ImageUrl ?? "",
        MessageKind.Order => "order:" + OrderId,
        _ => Content,
    };

    /// <summary>转发对应的 msgType，与 sendMessage 的参数一致</summary>
    public string ForwardType => Kind switch
    {
        MessageKind.Image => "image",
        MessageKind.Order => "order",
        _ => "text",
    };

    /// <summary>引用时写进输入框的摘要，非文本类型用占位词代替</summary>
    public string QuoteText => Kind switch
    {
        MessageKind.Image => "[图片]",
        MessageKind.Order => $"[订单 {CopyableOrderNo}]",
        MessageKind.Redpack => "[红包]",
        _ => Content,
    };

    /// <summary>领取成功后就地刷新卡片</summary>
    public void MarkRedpackReceived(int status = 1)
    {
        _redpackStatus = status;
        OnPropertyChanged(nameof(RedpackStatusText));
        OnPropertyChanged(nameof(CanReceiveRedpack));
    }

    private int _redpackStatus;

    /// <summary>
    /// 对方是否已读。单勾 = 已送达服务器，双勾 = 访客已读。
    /// 仅对自己（客服）发出的消息有意义，由轮询同步更新。
    /// </summary>
    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value) return;
            _isRead = value;
            OnPropertyChanged(nameof(IsRead));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 顶部的一个站点标签：绑一个站点就有一个标签。
/// 未读数来自该站点自己的未读轮询，当前正在看的那个高亮。
/// </summary>
public sealed class SiteTabItem : INotifyPropertyChanged
{
    public SiteTabItem(int index, string name)
    {
        Index = index;
        Name = name;
    }

    /// <summary>在主窗口站点列表里的下标，点击时按它切换</summary>
    public int Index { get; }

    public string Name { get; }

    private int _unread;

    public int Unread
    {
        get => _unread;
        set
        {
            if (_unread == value) return;
            _unread = value;
            OnPropertyChanged(nameof(Unread));
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadText));
        }
    }

    public bool HasUnread => _unread > 0;

    /// <summary>未读角标上的文字：超过 99 显示 99+</summary>
    public string UnreadText => _unread > 99 ? "99+" : _unread.ToString();

    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
