using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;
using Windowsyikefu.Models;
using Windowsyikefu.Services;
using Windowsyikefu.ViewModels;

namespace Windowsyikefu.Views;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    private const uint MB_ICONASTERISK = 0x00000040;

    // 窗口本体保持透明，玻璃感交给系统材质；各列自己决定透明或纯色
    private static readonly Brush GlassBackground = Brushes.Transparent;
    private static readonly Brush SolidBackground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF5));

    /// <summary>所有已绑定站点的服务实例：一个站点一个，各自独立轮询、互不干扰</summary>
    private readonly List<KefuService> _services;

    /// <summary>当前正在看的站点在 _services 里的下标</summary>
    private int _activeIndex;

    /// <summary>当前站点的服务。界面上的每个操作都只针对它</summary>
    private KefuService _svc => _services[_activeIndex];

    private readonly AppConfig _config;
    private readonly TrayService _tray;

    private readonly ObservableCollection<SessionItem> _sessions = new();
    private readonly ObservableCollection<MessageItem> _messages = new();
    private readonly ObservableCollection<string> _quickReplies = new();

    /// <summary>顶部站点标签：绑几个站点就有几项</summary>
    private readonly ObservableCollection<SiteTabItem> _siteTabs = new();

    /// <summary>快捷回复一行的高度（胶囊 26 + 下间距 8）</summary>
    private const double QuickReplyRowHeight = 34;
    private bool _quickExpanded;

    private readonly DispatcherTimer _listTimer = new();
    private readonly DispatcherTimer _chatTimer = new();

    private ICollectionView? _sessionView;
    private SessionItem? _current;
    private long _lastMessageId;

    /// <summary>
    /// 站点代次：每换一次站点 +1。会话列表、在线状态这些站点级请求都带着它回来校验，
    /// 对不上就整包丢弃——否则站点 A 的慢响应会写进站点 B 的界面。
    /// </summary>
    private long _siteToken;

    /// <summary>正在拉会话列表的站点。按站点互斥：切站点时新站点能立刻发请求，不会被上一个堵住</summary>
    private readonly HashSet<KefuService> _listBusy = new();

    /// <summary>
    /// 会话切换令牌：每换一次会话 +1。所有在飞的请求都带着发出时的令牌回来，
    /// 对不上就整包丢弃——否则快速来回切时，上一位访客的消息/标题会写进当前会话。
    /// </summary>
    private long _chatToken;

    /// <summary>消息轮询是否还在飞：只用于轮询之间互斥，切会话的全量请求不受它限制</summary>
    private bool _pollBusy;

    private bool _forceClose;

    /// <summary>消息 Id → 气泡项，用于按 Id 更新已读状态</summary>
    private readonly Dictionary<long, MessageItem> _messageIndex = new();

    /// <summary>拉不到详情的订单（已删除 / 无权限），记下来避免每轮轮询重复请求</summary>
    private readonly HashSet<long> _orderDetailFailed = new();
    private DateTime _lastReceiptSync = DateTime.MinValue;

    /// <summary>已读状态整会话校对的间隔（低频模式下放慢，少发一次是一次）</summary>
    private TimeSpan ReceiptSyncInterval => TimeSpan.FromSeconds(_config.LowFrequency ? 15 : 5);

    public MainWindow(List<KefuService> services, AppConfig config, TrayService tray)
    {
        _services = services;
        _config = config;
        _tray = tray;

        InitializeComponent();

        ApplyInitialSize();

        ListSessions.ItemsSource = _sessions;
        ListMessages.ItemsSource = _messages;
        ListQuickReplies.ItemsSource = _quickReplies;
        ListSiteTabs.ItemsSource = _siteTabs;
        foreach (var q in _config.QuickReplies) _quickReplies.Add(q);

        RebuildSiteTabs();

        // 短语被增删后重新判断要不要收起展开按钮
        _quickReplies.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(UpdateQuickReplyOverflow, DispatcherPriority.Loaded);

        TitleIcon.Source = LoadIconSource();

        // 图片消息可能是 "https://..." 也可能是 "/upload/x.webp"，相对路径要靠当前站点根地址补全
        MessageItem.ImageBaseUrl = _svc.Site.ApiUrl;

        _sessionView = CollectionViewSource.GetDefaultView(_sessions);
        _sessionView.Filter = FilterSession;

        // 每个站点各自订阅：后台站点的消息照样提醒，标签上也能看到它的未读
        foreach (var site in _services) SubscribeSite(site);

        // 每次 tick 后重新随机间隔（见 ApplyPollIntervals），所以这里不写死 Interval
        _listTimer.Tick += async (_, _) =>
        {
            ApplyPollIntervals();
            await RefreshSessionsAsync();
        };

        _chatTimer.Tick += async (_, _) =>
        {
            ApplyPollIntervals();
            await RefreshMessagesAsync(false);
        };

        ApplyPollIntervals();

        Loaded += async (_, _) =>
        {
            // 句柄此时已完全就绪，再兜一次，确保毛玻璃材质真的生效
            ApplyBackdrop();

            _listTimer.Start();
            UpdateChatChrome();
            UpdateOnlineVisual();
            await SyncOnlineStateAsync();
            await RefreshSessionsAsync();

            // 界面先出来、会话列表拉完，再悄悄查一次更新；
            // 查不到（断网 / 接口异常）当没这回事，照常使用
            _ = CheckUpdateOnStartupAsync();
        };

        Closing += OnClosing;
    }

    // ------------------------------------------------------------- 窗口外观

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ApplyBackdrop();

        // 无边框窗口（WindowStyle=None + WindowChrome）按系统默认最大化会连任务栏一起盖住，
        // 挂上 WM_GETMINMAXINFO 把最大尺寸压到显示器工作区，任务栏就不会被压掉。
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(MaxSizeHook);
    }

    /// <summary>
    /// 开启系统材质（Win11 Acrylic / Win10 亚克力）：成功就用透明底，让模糊透出来；
    /// 失败（老系统 / 系统关了透明效果）退回不透明底色，保证界面依然正常。
    /// 窗口句柄刚创建时偶尔设置不成功，所以 Loaded 里还会再兜一次。
    /// </summary>
    private void ApplyBackdrop()
        => Background = WindowEffects.TryEnableBackdrop(this) ? GlassBackground : SolidBackground;

    /// <summary>
    /// 默认窗口大小：目标 1360×860（比原来的 1100×720 大一圈，会话列表和聊天都更舒展），
    /// 但会按屏幕工作区收敛一下 —— 小屏 / 低分辨率下也保证完整显示、不压到任务栏。
    /// </summary>
    private void ApplyInitialSize()
    {
        var work = SystemParameters.WorkArea;

        Width = Math.Max(MinWidth, Math.Min(1360, work.Width - 40));
        Height = Math.Max(MinHeight, Math.Min(860, work.Height - 40));
    }

    /// <summary>
    /// 把内容面板裁成圆角（四个角可以不一样）。Border 自己的 CornerRadius 不会裁子元素
    /// （列表项的 hover 底色会盖住圆角，看起来就还是方的），
    /// 所以尺寸一变就按实际大小重设一次 Clip 几何，半径取自 Border 自身。
    /// </summary>
    private void PanelCornerClip(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border border) return;
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;

        var c = border.CornerRadius;
        if (c.TopLeft <= 0 && c.TopRight <= 0 && c.BottomRight <= 0 && c.BottomLeft <= 0) return;

        border.Clip = RoundedRect(e.NewSize.Width, e.NewSize.Height,
            c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft);
    }

    /// <summary>四角可以分别给半径的圆角矩形（RectangleGeometry 只能四角相同）</summary>
    private static Geometry RoundedRect(double w, double h, double tl, double tr, double br, double bl)
    {
        var figure = new PathFigure { StartPoint = new Point(tl, 0), IsClosed = true, IsFilled = true };

        figure.Segments.Add(new LineSegment(new Point(w - tr, 0), true));
        if (tr > 0)
            figure.Segments.Add(new ArcSegment(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true));

        figure.Segments.Add(new LineSegment(new Point(w, h - br), true));
        if (br > 0)
            figure.Segments.Add(new ArcSegment(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, true));

        figure.Segments.Add(new LineSegment(new Point(bl, h), true));
        if (bl > 0)
            figure.Segments.Add(new ArcSegment(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true));

        figure.Segments.Add(new LineSegment(new Point(0, tl), true));
        if (tl > 0)
            figure.Segments.Add(new ArcSegment(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true));

        return new PathGeometry(new[] { figure });
    }

    // ------------------------------------------------------------- 轮询节奏

    /// <summary>
    /// 会话列表 / 消息的刷新节奏。低频模式下整体放慢（列表 15 秒、消息 5 秒），
    /// 每次都重新算一遍并带 ±15% 抖动 —— 请求量少一大半，节奏也不再是死板的固定值，
    /// 被 CDN / WAF 当成高频爬虫拦掉的概率会低很多。
    /// </summary>
    private void ApplyPollIntervals()
    {
        var jitter = 0.85 + Random.Shared.NextDouble() * 0.3;

        _listTimer.Interval = TimeSpan.FromSeconds((_config.LowFrequency ? 15 : 6) * jitter);
        _chatTimer.Interval = TimeSpan.FromSeconds((_config.LowFrequency ? 5 : 2) * jitter);
    }

    // ------------------------------------------------------------- 拖动窗口

    private Point _dragStart;
    private bool _draggingWindow;

    /// <summary>这一轮按下落在滚动条之类的控件上，本次不参与拖动窗口</summary>
    private bool _dragBlocked;

    /// <summary>
    /// 按下时先只记起点，等真的移动了才 DragMove。
    /// 这样「按下→原地松开」仍然是点击（切标签、点在线开关、点按钮都不受影响），
    /// 而「按住→拖动」可以在整条左侧栏和顶部毛玻璃条上拖动窗口。
    /// 顶部的最小化 / 最大化 / 关闭那一块没有挂这套事件，保持纯按钮区。
    /// </summary>
    private void WindowDrag_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);

        // 滚动条 / 滑块自己就要用鼠标拖，别抢它们的手势
        _dragBlocked = e.OriginalSource is ScrollBar || e.OriginalSource is Thumb;
    }

    private void WindowDrag_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingWindow || _dragBlocked || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(this);

        // 没超过系统拖动阈值就当作点击，别抢走子元素的交互
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _draggingWindow = true;
        try
        {
            // 最大化状态下 DragMove 会变成「拖着最大化窗口」，先还原成普通大小再拖
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;

            DragMove();
        }
        catch
        {
            // 鼠标已经抬起 / 窗口状态不允许拖动时会抛，忽略即可
        }
        finally
        {
            _draggingWindow = false;
        }
    }

    // ------------------------------------------------------------- 最大化尺寸

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint Reserved;
        public WinPoint MaxSize;
        public WinPoint MaxPosition;
        public WinPoint MinTrackSize;
        public WinPoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WinRect Monitor;
        public WinRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// 最大化时只占「工作区」（不含任务栏）。多显示器下按窗口所在的那块屏算。
    /// </summary>
    private IntPtr MaxSizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        // ptMaxPosition 是相对显示器左上角的偏移，ptMaxSize 是最大化的宽高
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;

        // 手动拖拽边框时也别超过工作区，否则松手后一样会压住任务栏
        mmi.MaxTrackSize = mmi.MaxSize;

        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    private static ImageSource? LoadIconSource()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative));
            if (info?.Stream != null)
            {
                var decoder = new IconBitmapDecoder(info.Stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0) return decoder.Frames[0];
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------- 事件订阅

    /// <summary>某个站点订阅事件时用到的委托，退出时按它退订</summary>
    private sealed record SiteHandlers(
        Action<UnreadData> Unread,
        Action<KefuSession, string, long> Incoming,
        Action<string> Status,
        Action Sound);

    private readonly Dictionary<KefuService, SiteHandlers> _siteHandlers = new();

    /// <summary>把一个站点的服务接到界面上（每个站点都要接，后台站点也要有未读和提醒）</summary>
    private void SubscribeSite(KefuService site)
    {
        var handlers = new SiteHandlers(
            data => OnUnreadUpdated(site, data),
            (session, content, time) => OnIncomingMessage(site, session, content, time),
            text => OnStatusChanged(site, text),
            OnSoundRequested);

        site.UnreadUpdated += handlers.Unread;
        site.IncomingMessage += handlers.Incoming;
        site.StatusChanged += handlers.Status;
        site.SoundRequested += handlers.Sound;

        _siteHandlers[site] = handlers;
    }

    private void UnsubscribeSite(KefuService site)
    {
        if (!_siteHandlers.Remove(site, out var handlers)) return;

        site.UnreadUpdated -= handlers.Unread;
        site.IncomingMessage -= handlers.Incoming;
        site.StatusChanged -= handlers.Status;
        site.SoundRequested -= handlers.Sound;
    }

    private void OnStatusChanged(KefuService site, string text)
        => Dispatcher.Invoke(() =>
        {
            // 连接状态只反映当前正在看的那个站点
            if (!ReferenceEquals(site, _svc)) return;

            TxtConnStatus.Text = text;
            TxtConnStatus.Foreground = text.Contains("异常") || text.Contains("失败")
                ? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))
                : (Brush)FindResource("BrandHoverBrush");
        });

    private void OnSoundRequested()
    {
        if (!_config.SoundEnabled) return;
        try { MessageBeep(MB_ICONASTERISK); } catch { }
    }

    private void OnUnreadUpdated(KefuService site, UnreadData data)
        => Dispatcher.Invoke(() =>
        {
            // 标签上的未读红点 + 托盘显示所有站点的总和
            var index = _services.IndexOf(site);
            if (index >= 0 && index < _siteTabs.Count) _siteTabs[index].Unread = data.UnreadTotal;
            _tray.SetUnread(_siteTabs.Sum(t => t.Unread));

            // 只有当前站点的会话列表需要跟着更新
            if (!ReferenceEquals(site, _svc)) return;

            foreach (var s in data.Sessions ?? new List<KefuSession>())
            {
                var item = _sessions.FirstOrDefault(x => x.Id == s.Id);
                if (item != null) item.Update(s);
            }
        });

    private void OnIncomingMessage(KefuService site, KefuSession session, string content, long time)
        => Dispatcher.Invoke(() =>
        {
            if (_tray.IsPaused || !_config.ToastEnabled) return;

            var title = string.IsNullOrWhiteSpace(session.UserName)
                ? $"访客 {session.Id}"
                : session.UserName!.Trim();

            // 不是当前站点时，标题前面标出站点名，避免客服分不清是哪个网站来的
            if (!ReferenceEquals(site, _svc)) title = $"【{site.Site.DisplayName}】{title}";

            // 通知里带站点标识（不是下标）：中途增删站点后，点通知仍然找得回同一个站点
            if (!ToastService.ShowNewMessage(session.Id, time, title, content, siteKey: site.Site.Key))
                _tray.ShowBalloon($"新消息 · {title}", content);
        });

    /// <summary>在通知里快捷回复成功后刷新界面</summary>
    public void NotifyReplySent(long sessionId)
        => Dispatcher.Invoke(async () =>
        {
            await RefreshSessionsAsync();
            if (_current?.Id == sessionId) await RefreshMessagesAsync(false);
        });

    // ------------------------------------------------------------- 站点标签

    /// <summary>点标签：把整个界面切到那个站点</summary>
    private async void SiteTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SiteTabItem tab)
            await SwitchSiteAsync(tab.Index);
    }

    private static SiteTabItem? TabOf(object sender)
        => (sender as FrameworkElement)?.DataContext as SiteTabItem;

    /// <summary>标签右键菜单：刷新这个站点</summary>
    private async void SiteTabRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab || tab.Index >= _services.Count) return;

        var site = _services[tab.Index];
        site.RequestImmediateRefresh();
        _ = site.Api.HeartbeatAsync();

        if (tab.Index != _activeIndex) return;

        await RefreshSessionsAsync();
        if (_current != null) await RefreshMessagesAsync(true);
    }

    /// <summary>标签右键菜单：移除这个站点</summary>
    private async void SiteTabRemove_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab || tab.Index >= _services.Count) return;

        if (_services.Count <= 1)
        {
            MessageBox.Show(this, "至少要保留一个站点。", "移除站点",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var site = _services[tab.Index];
        var confirm = MessageBox.Show(this,
            $"确定要移除站点「{site.Site.DisplayName}」吗？移除后不再接收它的消息。",
            "移除站点", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        site.Stop();
        UnsubscribeSite(site);
        _services.RemoveAt(tab.Index);
        _config.Sites.Remove(site.Site);
        _config.Save();
        site.Dispose();

        // 活动下标跟着挪：删的在前面就前移一位；删的正好是当前站点就落到相邻的一个
        if (tab.Index < _activeIndex) _activeIndex--;
        else if (tab.Index == _activeIndex) _activeIndex = Math.Min(tab.Index, _services.Count - 1);

        RebuildSiteTabs();
        await ReloadActiveSiteAsync();
    }

    /// <summary>「+」：再绑一个网站，成功后多一个标签并直接切过去</summary>
    private async void BtnAddSite_Click(object sender, RoutedEventArgs e)
    {
        var win = new BindWindow(_config, null, append: true) { Owner = this };
        if (win.ShowDialog() != true || win.ResultSite is not { } site) return;

        // 同一个站点别绑两次（接口地址按统一规则比较，忽略大小写与末尾斜杠）
        if (_config.Sites.Any(s =>
                SiteConfig.SameUrl(s.ApiUrl, site.ApiUrl) &&
                string.Equals(s.AppId, site.AppId, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, "这个站点已经绑定过了。", "绑定站点",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _config.Sites.Add(site);
        _config.Save();

        var svc = new KefuService(site, _config)
        {
            // 托盘正处于「暂停接收」时，新加的站点也要跟着暂停，不能自己偷偷收消息
            Paused = _tray.IsPaused,
        };

        SubscribeSite(svc);
        svc.Start();
        _services.Add(svc);

        RebuildSiteTabs();
        await SwitchSiteAsync(_services.Count - 1);
    }

    /// <summary>按当前站点列表重建标签：绑了几个站点就有几个标签</summary>
    private void RebuildSiteTabs()
    {
        // 先把各站点当前的未读数记下来（按站点标识），
        // 否则增删一个站点会把其它站点的红点和托盘未读一起清零
        var unread = new Dictionary<string, int>();
        foreach (var tab in _siteTabs)
        {
            if (tab.Index >= 0 && tab.Index < _services.Count)
                unread[_services[tab.Index].Site.Key] = tab.Unread;
        }

        _siteTabs.Clear();

        for (var i = 0; i < _services.Count; i++)
        {
            var tab = new SiteTabItem(i, _services[i].Site.DisplayName);
            if (unread.TryGetValue(_services[i].Site.Key, out var count)) tab.Unread = count;
            _siteTabs.Add(tab);
        }

        UpdateSiteTabs();
    }

    /// <summary>高亮当前站点</summary>
    private void UpdateSiteTabs()
    {
        for (var i = 0; i < _siteTabs.Count; i++)
            _siteTabs[i].IsActive = i == _activeIndex;
    }

    /// <summary>切到第 index 个站点</summary>
    private async Task SwitchSiteAsync(int index)
    {
        if (index < 0 || index >= _services.Count || index == _activeIndex) return;

        // 旧站点马上就不是「正在查看」了，让它恢复正常弹通知
        _svc.ActiveSessionId = 0;
        _activeIndex = index;

        await ReloadActiveSiteAsync();
    }

    /// <summary>
    /// 把当前站点的数据整体重来一遍（切站点、移除站点都走这里）：
    /// 会话、消息、用户详情、客服头像、在线状态全部按新站点重建。
    /// </summary>
    private async Task ReloadActiveSiteAsync()
    {
        // 上一轮还在飞的请求全部作废，别的站点的回包不许落到新站点界面上
        _siteToken++;
        _chatToken++;
        _current = null;
        _sessions.Clear();
        _messages.Clear();
        _messageIndex.Clear();
        _lastMessageId = 0;
        _orderDetailFailed.Clear();
        _pollBusy = false;

        TxtSearch.Text = "";
        TxtInput.Clear();
        HideUserInfo();
        UpdateChatChrome();
        UpdateSiteTabs();

        // 不同站点的图片要按各自的站点根地址解析
        MessageItem.ImageBaseUrl = _svc.Site.ApiUrl;

        // 客服头像、在线开关都是站点级的，先退回应用图标再重新拉
        KefuAvatar.Source = null;
        KefuAvatarBox.Visibility = Visibility.Collapsed;

        // 上一个站点的上下线请求可能还在飞，这里把开关恢复成可用
        _onlineBusy = false;
        ChkOnline.IsEnabled = true;

        _online = false;
        UpdateOnlineVisual();
        _ = SyncOnlineStateAsync();

        await RefreshSessionsAsync();
    }

    // ------------------------------------------------------------- 会话列表

    private async Task RefreshSessionsAsync()
    {
        // 先记下这次请求属于哪个站点、哪一代：A 站点的慢响应绝不能写进 B 站点的界面
        var svc = _svc;
        var token = _siteToken;

        // 同一个站点同时只发一个列表请求；换站点后新站点可以立刻发，
        // 不会像以前那样被上一个站点的请求堵住（那样新站点会空着列表，旧会话还会赖在界面上）
        if (!_listBusy.Add(svc)) return;

        try
        {
            var result = await svc.Api.SessionListAsync();

            // 期间切了站点：整包丢弃
            if (_siteToken != token || !ReferenceEquals(svc, _svc)) return;

            var list = result.Data ?? new List<KefuSession>();

            var map = _sessions.ToDictionary(x => x.Id);
            foreach (var s in list)
            {
                // 头像只认这一路数据（sessionList）：进应用、6 秒定时、手动点刷新时更新
                if (map.TryGetValue(s.Id, out var item)) item.Update(s, updateAvatar: true);
                else _sessions.Add(new SessionItem(s));
            }

            var ids = list.Select(s => s.Id).ToHashSet();
            for (var i = _sessions.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(_sessions[i].Id) && _sessions[i].Id != _current?.Id)
                    _sessions.RemoveAt(i);
            }

            SortSessionsByTime();

            // 访客头像这一轮可能刚加载完，气泡旁边也同步一下
            SyncMessageAvatars();
        }
        catch
        {
            // 网络错误由 KefuService 的状态事件统一提示
        }
        finally
        {
            _listBusy.Remove(svc);
        }
    }

    /// <summary>按最后消息时间倒序，使用 Move 原地重排避免闪烁</summary>
    private void SortSessionsByTime()
    {
        for (var i = 1; i < _sessions.Count; i++)
        {
            var item = _sessions[i];
            var j = i - 1;
            while (j >= 0 && _sessions[j].LastTime < item.LastTime)
            {
                _sessions.Move(j, j + 1);
                j--;
            }
        }
    }

    private bool FilterSession(object obj)
    {
        if (string.IsNullOrWhiteSpace(TxtSearch.Text)) return true;
        if (obj is not SessionItem s) return false;
        var key = TxtSearch.Text.Trim();
        return (s.Title?.Contains(key, StringComparison.OrdinalIgnoreCase) ?? false)
            || (s.LastMsg?.Contains(key, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtSearchHint.Visibility = string.IsNullOrEmpty(TxtSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _sessionView?.Refresh();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _svc.RequestImmediateRefresh();

        // 手动刷新时顺带重新拉一次客服自己的头像 / 在线状态
        _ = SyncOnlineStateAsync();

        await RefreshSessionsAsync();
        if (_current != null) await RefreshMessagesAsync(true);
    }

    private async void ListSessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListSessions.SelectedItem is not SessionItem item)
        {
            _current = null;
            UpdateActiveSession();
            UpdateChatChrome();
            return;
        }

        if (_current?.Id == item.Id) return;

        _current = item;

        // 换会话 = 换一代请求：上一位访客还在飞的响应全部作废
        _chatToken++;

        _messages.Clear();
        _messageIndex.Clear();
        _lastMessageId = 0;

        TxtChatTitle.Text = item.Title;
        TxtChatSub.Text = item.StatusText;
        item.ClearUnread();
        HideUserInfo();

        UpdateActiveSession();
        UpdateChatChrome();
        await RefreshMessagesAsync(true);
        TxtInput.Focus();
    }

    // ------------------------------------------------------------- 聊天区状态

    /// <summary>根据是否选中会话，切换空状态与输入区的可用性</summary>
    private void UpdateChatChrome()
    {
        var has = _current != null;

        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ScrollMessages.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        QuickReplyBar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        InputBar.IsEnabled = has;
        InputBar.Opacity = has ? 1.0 : 0.45;

        if (!has)
        {
            TxtChatTitle.Text = "未选择会话";
            TxtChatSub.Text = "从左侧选择一个会话开始";
            HideUserInfo();
        }

        // 栏位显隐会改变可用宽度，重算一次候选词是否需要折叠
        Dispatcher.BeginInvoke(UpdateQuickReplyOverflow, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------- 快捷回复折叠

    /// <summary>宽度变化时重算：候选词是否超出一行</summary>
    private void QuickClip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateQuickReplyOverflow();
    }

    private void BtnQuickToggle_Click(object sender, RoutedEventArgs e)
    {
        _quickExpanded = !_quickExpanded;
        ApplyQuickReplyHeight();
    }

    /// <summary>
    /// 用不限制高度的测量结果判断候选词实际需要几行：
    /// 只有超过一行时才露出右侧的展开按钮。
    /// </summary>
    private void UpdateQuickReplyOverflow()
    {
        var width = QuickClip.ActualWidth;
        if (width <= 0) return;

        ListQuickReplies.Measure(new Size(width, double.PositiveInfinity));
        var overflows = ListQuickReplies.DesiredSize.Height > QuickReplyRowHeight + 0.5;

        BtnQuickToggle.Visibility = overflows ? Visibility.Visible : Visibility.Collapsed;

        // 内容已经能全部显示时，没必要保持展开状态
        if (!overflows && _quickExpanded) _quickExpanded = false;

        ApplyQuickReplyHeight();
    }

    private void ApplyQuickReplyHeight()
    {
        QuickClip.Height = _quickExpanded ? double.NaN : QuickReplyRowHeight;
        IconQuickToggle.Kind = _quickExpanded
            ? PackIconLucideKind.ChevronUp
            : PackIconLucideKind.ChevronDown;
        BtnQuickToggle.ToolTip = _quickExpanded ? "收起快捷回复" : "展开全部快捷回复";
    }

    private void TxtInput_TextChanged(object sender, TextChangedEventArgs e)
        => TxtInputHint.Visibility = string.IsNullOrEmpty(TxtInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ------------------------------------------------------------- 窗口置顶

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        BtnPin.Foreground = (Brush)FindResource(Topmost ? "BrandHoverBrush" : "TextPrimaryBrush");
        BtnPin.ToolTip = Topmost ? "已置顶，点击取消" : "窗口置顶";
    }

    // ------------------------------------------------------------- 设置

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_config, _services) { Owner = this };
        if (win.ShowDialog() != true) return;

        // 快捷回复短语可能在设置里被改过，重新载入
        _quickReplies.Clear();
        foreach (var q in _config.QuickReplies) _quickReplies.Add(q);

        _tray.SyncFromConfig();

        // 轮询节奏可能被改过（低频模式 / 间隔滑块），立刻生效
        ApplyPollIntervals();

        // 自定义请求头（防 CDN 拦截的暗号头）也可能被改过，立即应用到所有站点
        foreach (var svc in _services)
        {
            svc.Api.ApplyCustomHeaders(_config.CustomHeaders);
            svc.RequestImmediateRefresh();
        }

        // 暂停是全局的：只要还有站点在暂停，托盘就应显示「继续接收消息」
        _tray.SetPaused(_services.Any(x => x.Paused));

        _svc.RequestImmediateRefresh();
        UpdateActiveSession();
    }

    // ------------------------------------------------------------- 更新检查

    /// <summary>
    /// 进程序后自动查一次更新。
    /// 普通更新：可以「稍后再说」；强制更新：只有「立即下载」，打开下载页后退出程序，
    /// 必须装上新版才能继续用。
    /// 网络不通 / 接口返回异常时静默跳过，绝不能因此挡住正常使用。
    /// </summary>
    private async Task CheckUpdateOnStartupAsync()
    {
        try
        {
            // 等界面和会话列表先就绪，别和启动抢网络
            await Task.Delay(TimeSpan.FromSeconds(3));

            var info = await UpdateService.CheckAsync();
            if (info is not { CanDownload: true }) return;

            // 开机自启 / 托盘后台启动时窗口是隐藏的：不弹窗打扰，
            // 只在强制更新时用托盘气泡提醒一下
            if (!IsVisible)
            {
                if (info.Force)
                    _tray.ShowBalloon("需要更新",
                        $"当前版本 {UpdateService.CurrentVersion}，请下载新版本：{info.DownloadUrl}");

                return;
            }

            if (info.Force)
            {
                MessageBox.Show(this,
                    UpdateService.BuildMessage(info),
                    $"必须更新到 {info.Version}",
                    MessageBoxButton.OK, MessageBoxImage.Warning);

                if (UpdateService.OpenDownload(info.DownloadUrl!))
                {
                    // 强制更新：下载页打开后直接退出。
                    // 必须先置 _forceClose，否则会走「关闭到托盘」那条路，等于没退。
                    _forceClose = true;
                    Application.Current.Shutdown();
                }
                else
                {
                    _tray.ShowBalloon("需要更新", $"请手动下载新版本：{info.DownloadUrl}");
                }

                return;
            }

            var result = MessageBox.Show(this,
                UpdateService.BuildMessage(info),
                $"发现新版本 {info.Version}",
                MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (result == MessageBoxResult.OK) UpdateService.OpenDownload(info.DownloadUrl!);
        }
        catch
        {
            // 网站访问不了、超时、返回不是预期格式：一律当没有更新，正常使用
        }
    }

    // ------------------------------------------------------------- 消息

    /// <summary>
    /// 订单消息服务端只回 "order:订单ID" 时，卡片上没有订单号等信息可预览。
    /// 这里对缺数据的订单卡片补拉一次 orderInfo，补到就地刷新，补不到就静默跳过。
    /// </summary>
    private async Task EnrichOrderCardsAsync(long sessionId)
    {
        await Task.Yield();

        var pending = _messages
            .Where(m => m.Kind == MessageKind.Order
                        && m.NeedsOrderDetail
                        && !_orderDetailFailed.Contains(m.OrderId))
            .ToList();

        foreach (var msg in pending)
        {
            if (_current?.Id != sessionId) return;          // 已切会话，别再补了

            try
            {
                var env = await _svc.Api.OrderInfoAsync(msg.OrderId);
                if (!msg.ApplyOrderInfo(env.Data)) _orderDetailFailed.Add(msg.OrderId);
            }
            catch
            {
                // 订单不存在 / 无权限：保留 — 占位即可，不弹窗打扰客服
                _orderDetailFailed.Add(msg.OrderId);
            }
        }
    }

    private async Task RefreshMessagesAsync(bool full)
    {
        if (_current is not { } session) return;

        var sessionId = session.Id;

        // 这次请求属于哪一代会话：回来时要靠它判断结果还算不算数
        var token = _chatToken;

        // 轮询之间互斥（上一次没回来就跳过这一拍）；
        // 全量请求不受限制——快速切换时必须让新会话立刻发请求，
        // 用旧的「正在加载就 return」会把新会话的加载直接吞掉，界面上留着的还是上一位访客的内容。
        if (!full)
        {
            if (_pollBusy) return;
            _pollBusy = true;
        }

        try
        {
            var result = await _svc.Api.SessionInfoAsync(sessionId, full ? 0 : _lastMessageId);
            var data = result.Data;
            if (data == null) return;

            // await 期间可能已经切到别的会话：这一包整个丢弃，绝不能往界面上写
            if (_chatToken != token || _current?.Id != sessionId) return;

            if (data.Session != null) session.Update(data.Session);

            if (full)
            {
                _messages.Clear();
                _messageIndex.Clear();
                _lastMessageId = 0;
            }

            var added = false;
            foreach (var m in data.Messages ?? new List<KefuMessage>())
            {
                if (m.Id <= _lastMessageId) continue;
                var item = new MessageItem(m);
                _messages.Add(item);
                _messageIndex[m.Id] = item;
                _lastMessageId = m.Id;
                added = true;
            }

            if (added)
            {
                ScrollToEnd();
                _ = EnrichOrderCardsAsync(sessionId);
            }

            // 气泡旁边的头像跟着当前会话走（新加的消息要补上）
            SyncMessageAvatars();

            // 服务端只在全量返回时才带最新的 isread；这里对「自己发出、但还没确认已读」
            // 的消息定期做一次整会话校对，让单勾及时变成双勾。
            if (!full && HasPendingReceipt() &&
                DateTime.Now - _lastReceiptSync >= ReceiptSyncInterval)
            {
                await SyncReadStatesAsync(sessionId);
            }
        }
        catch
        {
            // 忽略瞬时错误
        }
        finally
        {
            if (!full) _pollBusy = false;
        }
    }

    private bool HasPendingReceipt()
    {
        foreach (var m in _messages)
            if (m.IsMine && !m.IsRead) return true;

        return false;
    }

    /// <summary>用全量消息回填本地气泡的已读状态（只作用于指定会话）</summary>
    private async Task SyncReadStatesAsync(long sessionId)
    {
        if (_current?.Id != sessionId) return;

        var svc = _svc;

        // 先记时间：即便这次失败，也不要在下个 2 秒周期立刻重试，避免请求风暴
        _lastReceiptSync = DateTime.Now;

        try
        {
            var result = await svc.Api.SessionInfoAsync(sessionId, 0);

            // 拉取期间切了会话或站点就别回写了，
            // 否则会把已读状态盖到别的会话 / 别的站点的气泡上
            if (!ReferenceEquals(svc, _svc) || _current?.Id != sessionId) return;

            foreach (var m in result.Data?.Messages ?? new List<KefuMessage>())
            {
                if (_messageIndex.TryGetValue(m.Id, out var item))
                    item.IsRead = m.IsRead == 1;
            }
        }
        catch
        {
            // 忽略瞬时错误，下个周期再试
        }
    }

    private void ScrollToEnd()
        => Dispatcher.BeginInvoke(new Action(() => ScrollMessages.ScrollToEnd()),
            DispatcherPriority.Background);

    private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private void TxtInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendCurrentAsync();
        }
    }

    private async Task SendCurrentAsync()
    {
        if (_current == null) return;

        var sessionId = _current.Id;
        var text = TxtInput.Text.Trim();
        if (text.Length == 0) return;

        TxtInput.IsEnabled = false;
        BtnSend.IsEnabled = false;

        try
        {
            await _svc.Api.SendMessageAsync(sessionId, text);
            TxtInput.Clear();

            // 发送期间切走了就不要再刷新了，否则会把别的会话的消息拉进来
            if (_current?.Id == sessionId) await RefreshMessagesAsync(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TxtInput.IsEnabled = true;
            BtnSend.IsEnabled = true;
            TxtInput.Focus();
        }
    }

    private void QuickReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Content is not string text) return;

        var existing = TxtInput.Text;
        TxtInput.Text = string.IsNullOrWhiteSpace(existing) ? text : existing.TrimEnd() + text;
        TxtInput.CaretIndex = TxtInput.Text.Length;
        TxtInput.Focus();
    }

    private async void BtnCloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } session) return;

        var sessionId = session.Id;

        var confirm = MessageBox.Show(this,
            $"确定要结束与「{session.Title}」的会话吗？结束后将追加一条系统消息。",
            "结束会话", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _svc.Api.CloseSessionAsync(sessionId);
            if (_current?.Id == sessionId) await RefreshMessagesAsync(true);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------- 图片 / 订单 / 红包

    /// <summary>选本地图片 → uploadImage 上传 → 以 image 类型发出</summary>
    private async void BtnPickImage_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } session) return;

        // 记住这张图要发给谁、发到哪个站点：上传期间用户可能已经切走，
        // 用 _current / _svc 实时取就会发错人或发错站点
        var sessionId = session.Id;
        var svc = _svc;
        var token = _siteToken;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送的图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp",
        };

        if (dlg.ShowDialog(this) != true) return;

        BtnPickImage.IsEnabled = false;

        try
        {
            var uploaded = await svc.Api.UploadImageAsync(dlg.FileName);
            var url = uploaded.Data?.Url;

            if (_siteToken != token || !ReferenceEquals(svc, _svc)) return; // 已切站点，这张图就别发了

            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(this, "上传成功，但服务端没有返回图片地址。",
                    "发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await svc.Api.SendMessageAsync(sessionId, url!, "image");

            if (_current?.Id == sessionId) await RefreshMessagesAsync(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "图片发送失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnPickImage.IsEnabled = true;
        }
    }

    /// <summary>点图片气泡：交给系统默认看图程序打开</summary>
    private void MessageImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MessageItem msg) OpenImage(msg);
    }

    private void OpenImage(MessageItem msg)
    {
        if (string.IsNullOrWhiteSpace(msg.AbsoluteImageUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(msg.AbsoluteImageUrl!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开图片失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- 右键菜单

    /// <summary>菜单项自身没有消息对象，从 DataContext 取（ContextMenu 绑的是 PlacementTarget.DataContext）</summary>
    private static MessageItem? MessageOf(object sender)
        => (sender as MenuItem)?.DataContext as MessageItem;

    /// <summary>右键会话项：先选中再弹菜单，菜单里的操作才会作用于这个会话</summary>
    private void SessionItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) item.IsSelected = true;
    }

    /// <summary>复制到剪贴板。剪贴板偶尔被别的程序占用，失败要明确提示，别静默吞掉</summary>
    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "复制",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MsgCopyText_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(MessageOf(sender)?.Content);

    private void MsgCopyOrderNo_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(MessageOf(sender)?.CopyableOrderNo);

    private void MsgCopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender)?.Image is not BitmapSource bmp) return;

        try
        {
            Clipboard.SetImage(bmp);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "复制失败：" + ex.Message, "复制图片",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>图片另存为：图已解码在内存里，直接编码写盘，不用再走一次网络</summary>
    private void MsgSaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender)?.Image is not BitmapSource bmp) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存图片",
            FileName = $"kefu-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using var fs = File.Create(dlg.FileName);
            BitmapEncoder encoder = dlg.FilterIndex == 2
                ? new JpegBitmapEncoder()
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存图片失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MsgOpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is { } msg) OpenImage(msg);
    }

    /// <summary>
    /// 引用：把「谁 · 时间 · 内容摘要」插到输入框最前面，光标停在末尾，
    /// 接着往下打字就是一条带上下文的回复。
    /// </summary>
    private void MsgQuote_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || MessageOf(sender) is not { } msg) return;

        var who = msg.IsMine ? "我" : _current.Title;
        TxtInput.Text = $"> {who} {msg.TimeText}：{msg.QuoteText}{Environment.NewLine}{TxtInput.Text}";
        TxtInput.CaretIndex = TxtInput.Text.Length;
        TxtInput.Focus();
    }

    /// <summary>转发：挑一个其它会话，把这条消息原样重发过去</summary>
    private async void MsgForward_Click(object sender, RoutedEventArgs e)
    {
        if (MessageOf(sender) is not { } msg) return;

        var targets = _sessions.Where(x => x.Id != _current?.Id).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "没有其它可转发的会话。", "转发",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new ForwardWindow(targets, $"将转发：{msg.QuoteText}") { Owner = this };
        if (win.ShowDialog() != true || win.TargetSessionId == 0) return;

        try
        {
            await _svc.Api.SendMessageAsync(win.TargetSessionId, msg.ForwardContent, msg.ForwardType);

            await RefreshSessionsAsync();
            _tray.ShowBalloon("转发成功", $"已转发给「{win.TargetTitle}」");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "转发失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 会话列表右键的「查看用户详情」：与「⋯」菜单的切换语义不同，
    /// 这里是要「看」，面板已经开着就别收起来。
    /// </summary>
    private void SessionShowUserInfo_Click(object sender, RoutedEventArgs e) => ShowUserInfo(true);

    private void SessionCopyName_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard((sender as MenuItem)?.DataContext is SessionItem s ? s.Title : null);

    private void SessionCopyId_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard((sender as MenuItem)?.DataContext is SessionItem s ? s.Id.ToString() : null);

    private async void MenuSendOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        var win = new OrderSearchWindow(_svc.Api, _current.Id) { Owner = this };
        if (win.ShowDialog() == true && win.Sent) await RefreshMessagesAsync(false);
    }

    private async void MenuSendRedpack_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        var win = new RedpackSendWindow(_svc.Api, _current.Id) { Owner = this };
        if (win.ShowDialog() == true && win.Sent) await RefreshMessagesAsync(false);
    }

    private async void MenuReopenSession_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } session) return;

        var sessionId = session.Id;

        try
        {
            await _svc.Api.ReopenSessionAsync(sessionId);
            if (_current?.Id == sessionId) await RefreshMessagesAsync(true);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重开会话失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>订单卡片：查看详情</summary>
    private async void OrderDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MessageItem msg || msg.OrderId == 0)
            return;

        try
        {
            var env = await _svc.Api.OrderInfoAsync(msg.OrderId);
            new InfoDialog($"订单详情（ID {msg.OrderId}）", InfoDialog.FromJson(env.Data),
                icon: PackIconLucideKind.Package) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "获取订单失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>订单卡片：退款（金额留空表示按系统规则全额退，需订单处理权限）</summary>
    private async void OrderRefund_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MessageItem msg || msg.OrderId == 0)
            return;

        var confirm = MessageBox.Show(this,
            $"确定对订单 {msg.OrderId} 执行退款吗？\n退款金额按系统规则计算，操作不可撤销。",
            "订单退款", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var env = await _svc.Api.OrderRefundAsync(msg.OrderId);
            new InfoDialog("退款结果", InfoDialog.FromJson(env.Data), env.Msg,
                PackIconLucideKind.CircleCheck) { Owner = this }.ShowDialog();

            await RefreshMessagesAsync(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "退款失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>订单卡片：补单</summary>
    private async void OrderReorder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MessageItem msg || msg.OrderId == 0)
            return;

        var confirm = MessageBox.Show(this,
            $"确定对订单 {msg.OrderId} 执行补单吗？\n补单会按后台「支付管理 → 补单」的规则重新处理该订单。",
            "订单补单", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var env = await _svc.Api.OrderReorderAsync(msg.OrderId);
            new InfoDialog("补单结果", InfoDialog.FromJson(env.Data), env.Msg,
                PackIconLucideKind.CircleCheck) { Owner = this }.ShowDialog();

            await RefreshMessagesAsync(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "补单失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>领取访客发来的红包</summary>
    private async void RedpackReceive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MessageItem msg || msg.RedpackId == 0)
            return;

        try
        {
            var env = await _svc.Api.RedpackReceiveAsync(msg.RedpackId);
            msg.MarkRedpackReceived();

            new InfoDialog("领取成功", InfoDialog.FromJson(env.Data), env.Msg,
                PackIconLucideKind.Wallet) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "领取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------- 上 / 下线

    private bool _online = true;
    private bool _onlineBusy;

    /// <summary>
    /// 头像下方的在线开关：点一下在线、再点一下离线。
    /// 走服务端 setOnline 接口切换"客服身份是否在线"，先成功再改本地显示，失败自动回滚。
    /// </summary>
    private async void ChkOnline_Click(object sender, RoutedEventArgs e)
    {
        if (_onlineBusy)
        {
            UpdateOnlineVisual(); // 请求还没回来又点了一次：把开关摆回真实状态
            return;
        }

        var target = ChkOnline.IsChecked == true;
        if (target == _online) return; // 状态没变化，不做无谓请求

        // 开关操作只作用于点击时所处的站点：请求飞行期间切走了，就地放弃，
        // 否则会把上一个站点的状态写到新站点的界面上
        var svc = _svc;
        var token = _siteToken;

        _onlineBusy = true;
        ChkOnline.IsEnabled = false;
        TxtOnlineState.Text = "切换中…";

        try
        {
            await svc.Api.SetOnlineAsync(target);

            if (_siteToken != token || !ReferenceEquals(svc, _svc)) return;
            _online = target;
        }
        catch (Exception ex)
        {
            if (_siteToken != token || !ReferenceEquals(svc, _svc)) return;

            MessageBox.Show(this, ex.Message, target ? "上线失败" : "下线失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _onlineBusy = false;

            if (_siteToken == token && ReferenceEquals(svc, _svc))
            {
                ChkOnline.IsEnabled = true;
                UpdateOnlineVisual(); // 成功刷新为新状态，失败回滚到原状态
            }
        }
    }

    private void UpdateOnlineVisual()
    {
        ChkOnline.IsChecked = _online;
        ChkOnline.ToolTip = _online ? "当前在线 · 点击下线" : "当前离线 · 点击上线";

        TxtOnlineState.Text = _online ? "在线" : "离线";
        TxtOnlineState.Foreground = (Brush)FindResource(_online ? "BrandHoverBrush" : "TextSecondaryBrush");

        // 头像右下角的小圆点跟着在线状态走
        KefuOnlineDot.Fill = (Brush)FindResource(_online ? "BrandHoverBrush" : "TextSecondaryBrush");
    }

    /// <summary>
    /// 启动时用服务端 kefuInfo 拉一次：既初始化在线开关（避免重启后显示与实际不一致），
    /// 也顺手把客服自己的真实头像换到左侧栏顶部。
    /// </summary>
    private async Task SyncOnlineStateAsync()
    {
        // 这次请求针对哪个站点：在线状态和客服头像都是站点级的，回来时必须对得上
        var svc = _svc;
        var token = _siteToken;

        try
        {
            var info = await svc.Api.KefuInfoAsync();

            // 期间切了站点：这份信息属于上一个站点，不能写到当前界面上
            if (_siteToken != token || !ReferenceEquals(svc, _svc)) return;

            if (info.Data is { } kefu)
            {
                _online = kefu.Online == 1;
                UpdateOnlineVisual();
                ApplyKefuAvatar(kefu.Avatar);
            }
        }
        catch { /* 拉不到就沿用当前显示 */ }
    }

    // ------------------------------------------------------------- 头像

    /// <summary>
    /// 左侧栏顶部的客服头像：拿到 kefuInfo.avatar 就显示真实头像，
    /// 地址为空或加载失败时保留应用图标，绝不显示空白。
    /// </summary>
    private void ApplyKefuAvatar(string? url)
    {
        var svc = _svc;
        var token = _siteToken;

        void Show(ImageSource img)
        {
            // 头像是后台解码完才回调的，回来时可能已经切到别的站点了
            if (_siteToken != token || !ReferenceEquals(svc, _svc)) return;

            KefuAvatar.Source = img;
            KefuAvatarBox.Visibility = Visibility.Visible;

            // 自己的头像到位后，聊天气泡旁也跟着换
            SyncMessageAvatars();
        }

        if (ImageUtil.Load(url, Show) is { } ready) Show(ready);
    }

    /// <summary>
    /// 把「当前访客的头像」和「客服自己的头像」贴到每条消息上（气泡左右那两个头像）。
    /// 头像跟着会话走，所以切会话、列表刷新、头像加载完都要重贴一次；
    /// 值没变时 setter 不发通知，重复调用没有开销。
    /// </summary>
    private void SyncMessageAvatars()
    {
        var visitor = _current?.Avatar;
        var mine = KefuAvatar.Source;

        foreach (var m in _messages)
            m.Avatar = m.IsMine ? mine : visitor;
    }

    /// <summary>
    /// 用户详情面板的头像：有图用图，没图回退空头像占位。
    /// localReady 是列表里已经加载好的那张图，传进来就是为了让面板立刻有图，
    /// 不用等 userInfo 接口回来，也就不会出现「先空白两秒再出图」。
    /// </summary>
    private void ApplyInfoAvatar(string? url, ImageSource? localReady = null)
    {
        void Show(ImageSource img)
        {
            InfoAvatarImage.Source = img;
            InfoAvatarBox.Visibility = Visibility.Collapsed;
            InfoAvatarImageBox.Visibility = Visibility.Visible;
        }

        if (localReady != null)
        {
            Show(localReady);
            return;
        }

        var ready = ImageUtil.Load(url, img =>
        {
            // 下载期间可能已经切到别的会话了，晚到的图不能盖到新会话头上
            if (!string.Equals(_current?.AvatarUrl, url, StringComparison.Ordinal)) return;
            Show(img);
        });

        if (ready != null)
        {
            Show(ready);
            return;
        }

        // 还没拿到图：只有面板当前确实空着才退回占位，别把已经显示的头像擦掉
        if (InfoAvatarImage.Source == null)
        {
            InfoAvatarBox.Visibility = Visibility.Visible;
            InfoAvatarImageBox.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------- 会话详情

    /// <summary>点「⋯」弹出菜单（用户详情 / 订单 / 红包 / 结束会话）</summary>
    private void BtnMore_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || BtnMore.ContextMenu is not { } menu) return;

        // 只有已结束的会话才谈得上「重新打开」
        MenuReopenSession.IsEnabled = _current.Status == 2;

        menu.PlacementTarget = BtnMore;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MenuUserInfo_Click(object sender, RoutedEventArgs e)
        => ShowUserInfo(UserInfoPanel.Visibility != Visibility.Visible);

    /// <summary>用户详情面板从右侧滑入 / 收回</summary>
    private void ShowUserInfo(bool show)
    {
        if (show) FillUserInfo();

        var anim = new DoubleAnimation(
            show ? 0 : UserInfoPanel.Width,
            TimeSpan.FromMilliseconds(show ? 220 : 160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (show)
        {
            UserInfoPanel.Visibility = Visibility.Visible;
        }
        else
        {
            anim.Completed += (_, _) => UserInfoPanel.Visibility = Visibility.Collapsed;
        }

        UserInfoShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void HideUserInfo()
    {
        if (UserInfoPanel.Visibility == Visibility.Visible) ShowUserInfo(false);
    }

    private void FillUserInfo()
    {
        if (_current == null) return;

        // 先清掉上一位访客的图，再优先用会话列表里已加载好的那张：
        // 既不会串脸，也不会出现打开面板后空白两秒
        InfoAvatarImage.Source = null;
        ApplyInfoAvatar(_current.AvatarUrl, _current.Avatar);

        InfoTitle.Text = _current.Title;
        InfoStatus.Text = _current.StatusText;
        InfoId.Text = $"#{_current.Id}";
        InfoLastMsg.Text = string.IsNullOrWhiteSpace(_current.LastMsg) ? "（暂无消息）" : _current.LastMsg;
        InfoLastTime.Text = TimeUtil.ToMessageText(_current.LastTime);
        InfoVisitorState.Text = _current.VisitorStateText;

        BtnFullUserInfo.IsEnabled = true;
        _userInfoCache = null;
        _ = LoadUserInfoAsync(_current.Id);
    }

    /// <summary>最近一次 userInfo 结果，供「查看完整资料」展示</summary>
    private JsonElement? _userInfoCache;

    /// <summary>
    /// 用 userInfo 把面板上的昵称、手机号等信息补全。
    /// 这个接口失败不影响已经在显示的内容，所以静默处理。
    /// </summary>
    private async Task LoadUserInfoAsync(long sessionId)
    {
        var svc = _svc;

        try
        {
            var env = await svc.Api.UserInfoAsync(sessionId);

            // 面板可能已经被关掉，或者切到了别的会话 / 别的站点
            if (!ReferenceEquals(svc, _svc) || _current?.Id != sessionId) return;

            _userInfoCache = env.Data;

            var nickname = JsonUtil.Pick(env.Data, "nickname", "username", "name", "user");
            if (!string.IsNullOrWhiteSpace(nickname)) InfoTitle.Text = nickname!;

            // 头像统一由会话列表那条链路负责：这里只在列表还没拿到头像时才采纳，
            // 避免点开详情面板就把头像换成另一张（服务端两处字段偶尔不一致）
            var avatarUrl = JsonUtil.Pick(env.Data, "avatar", "user_avatar");
            if (!string.IsNullOrWhiteSpace(avatarUrl) && _current.Avatar == null)
                ApplyInfoAvatar(avatarUrl);

            // 在线状态同样以 userInfo 为准，比会话列表里的快照更实时
            if (env.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                env.Data.TryGetProperty("user_online", out var onlineEl))
                InfoVisitorState.Text = JsonUtil.Bool(onlineEl)
                    ? "在线"
                    : (_current.UserLastActive > 0
                        ? $"离线 · 最后活跃 {TimeUtil.ToMessageText(_current.UserLastActive)}"
                        : "离线");

            InfoStatus.Text = _current.StatusText;
        }
        catch
        {
            // 拿不到详情就保持会话列表里的信息，不打扰客服
        }
    }

    /// <summary>用户详情面板里的「查看完整资料」</summary>
    private async void BtnFullUserInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } session) return;

        var sessionId = session.Id;
        var title = session.Title;
        var svc = _svc;

        if (_userInfoCache is not { } cached || cached.ValueKind != JsonValueKind.Object)
        {
            try
            {
                var env = await svc.Api.UserInfoAsync(sessionId);

                // 拉取期间切了会话 / 站点：这份资料属于别人，不能用也不能缓存
                if (!ReferenceEquals(svc, _svc) || _current?.Id != sessionId) return;

                _userInfoCache = env.Data;
                cached = env.Data;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "获取用户资料失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        new InfoDialog($"用户资料（{title}）", InfoDialog.FromJson(cached),
            icon: PackIconLucideKind.UserRound) { Owner = this }.ShowDialog();
    }

    // ------------------------------------------------------------- 窗口行为

    public void ShowFromTray(long? openSessionId = null) => ShowFromTray((string?)null, openSessionId);

    /// <summary>
    /// 从托盘 / 通知回到界面。siteKey 非空时先切到那个站点再定位会话，
    /// 所以通知点开的一定是消息所属站点里的那个会话。
    /// </summary>
    public void ShowFromTray(string? siteKey, long? openSessionId)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;

        _ = ShowFromTrayAsync(siteKey, openSessionId);
    }

    private async Task ShowFromTrayAsync(string? siteKey, long? openSessionId)
    {
        if (!string.IsNullOrWhiteSpace(siteKey))
        {
            var index = _services.FindIndex(s => s.Site.Key == siteKey);

            // 找不到说明这个站点已经被移除了：那就只在当前站点找会话，别乱切
            if (index >= 0 && index != _activeIndex) await SwitchSiteAsync(index);
        }

        if (openSessionId.HasValue) await OpenSessionAsync(openSessionId.Value);
        else UpdateActiveSession();
    }

    /// <summary>从通知点进来时，会话列表可能还没加载，先拉一次再定位</summary>
    private async Task OpenSessionAsync(long sessionId)
    {
        if (_sessions.Count == 0) await RefreshSessionsAsync();
        SelectSession(sessionId);
        UpdateActiveSession();
    }

    public void SelectSession(long sessionId)
    {
        var item = _sessions.FirstOrDefault(x => x.Id == sessionId);
        if (item == null) return;

        if (!string.IsNullOrWhiteSpace(TxtSearch.Text))
        {
            TxtSearch.Text = "";
            _sessionView?.Refresh();
        }

        ListSessions.SelectedItem = item;
        ListSessions.ScrollIntoView(item);
    }

    /// <summary>只有窗口可见且未最小化时，当前会话才算「正在查看」，否则照常弹通知</summary>
    private void UpdateActiveSession()
    {
        var active = IsVisible && WindowState != WindowState.Minimized && _current != null;
        _svc.ActiveSessionId = active ? _current!.Id : 0;

        if (active)
        {
            _svc.ClearNotifyState(_current!.Id);
            if (!_chatTimer.IsEnabled) _chatTimer.Start();
        }
        else
        {
            _chatTimer.Stop();
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        // 最大化时换成「还原」图标（两个叠放的方块），否则显示「最大化」方块
        IconMax.Kind = WindowState == WindowState.Maximized
            ? PackIconLucideKind.Copy
            : PackIconLucideKind.Square;

        UpdateActiveSession();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose || !_tray.CloseToTray)
        {
            Detach();
            return;
        }

        e.Cancel = true;
        Hide();
        UpdateActiveSession();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void Detach()
    {
        _listTimer.Stop();
        _chatTimer.Stop();

        foreach (var site in _services) UnsubscribeSite(site);
    }
}
