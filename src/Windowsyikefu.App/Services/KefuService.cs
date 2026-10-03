using Windowsyikefu.Models;

namespace Windowsyikefu.Services;

/// <summary>
/// 核心调度：负责未读轮询、心跳、通知派发。
/// UI 通过 <see cref="Api"/> 直接调用会话/消息接口。
/// </summary>
public sealed class KefuService : IDisposable
{
    private readonly Dictionary<long, long> _notifiedLastTime = new();
    private CancellationTokenSource? _cts;
    private int _heartbeatTicks;

    public AppConfig Config { get; }
    public KefuApiClient Api { get; }
    public KefuInfo? Kefu { get; private set; }

    /// <summary>这个服务负责的站点（一个站点一个实例，各自轮询互不干扰）</summary>
    public SiteConfig Site { get; }

    /// <summary>当前正打开的会话（该会话不弹通知）</summary>
    public long ActiveSessionId { get; set; }

    /// <summary>暂停接收（托盘菜单可切换）</summary>
    public bool Paused { get; set; }

    /// <summary>连接状态文本变化</summary>
    public event Action<string>? StatusChanged;

    /// <summary>未读汇总更新（未读总数、未读会话数）</summary>
    public event Action<UnreadData>? UnreadUpdated;

    /// <summary>收到新消息，需要提醒（会话, 消息内容, 消息时间）</summary>
    public event Action<KefuSession, string, long>? IncomingMessage;

    /// <summary>提示音请求</summary>
    public event Action? SoundRequested;

    public KefuService(SiteConfig site, AppConfig config)
    {
        Site = site;
        Config = config;
        Api = new KefuApiClient(site.ApiUrl, site.AppId, site.AppSecret);

        // 带上「暗号头」（防 CDN 拦截用的自定义请求头）
        Api.ApplyCustomHeaders(config.CustomHeaders);
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>立即触发一次刷新（例如手动点击刷新按钮）</summary>
    public void RequestImmediateRefresh() => _wake?.TrySetResult(true);

    private TaskCompletionSource<bool>? _wake;

    private async Task LoopAsync(CancellationToken ct)
    {
        // 绑定多个站点时错开起跑，避免同一瞬间并发出好几个请求
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1500)), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 启动时先刷新客服信息
        try
        {
            var info = await Api.KefuInfoAsync(ct);
            Kefu = info.Data;
            StatusChanged?.Invoke(Kefu?.Online == 1 ? "在线" : "已连接");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            StatusChanged?.Invoke("连接失败：" + ex.Message);
        }

        while (!ct.IsCancellationRequested)
        {
            var interval = NextInterval();
            _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!Paused)
            {
                try
                {
                    await PollOnceAsync(ct);
                    _failStreak = 0;
                    StatusChanged?.Invoke(Kefu?.Online == 1 ? "在线" : "已连接");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _failStreak++;
                    StatusChanged?.Invoke("网络异常：" + ex.Message);
                }
            }
            else
            {
                StatusChanged?.Invoke("已暂停接收");
            }

            try
            {
                var delay = Task.Delay(interval, ct);
                await Task.WhenAny(delay, _wake.Task);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>连续失败次数，用于退避（被限速 / 断网时别再按原节奏猛敲）</summary>
    private int _failStreak;

    /// <summary>
    /// 下一次轮询的间隔：
    /// 1) 低频模式至少 8 秒；
    /// 2) 乘 ±15% 随机抖动 —— 完全固定的节奏是最明显的机器人特征，抖动后请求分布更像真人；
    /// 3) 连续失败时成倍放慢（最多 4 倍），避免被限速后继续高频请求把封禁坐实。
    /// </summary>
    private TimeSpan NextInterval()
    {
        double seconds = Math.Clamp(Config.PollSeconds, 2, 30);
        if (Config.LowFrequency) seconds = Math.Max(seconds, 8);

        if (_failStreak > 0) seconds *= Math.Min(_failStreak + 1, 4);

        seconds *= 0.85 + Random.Shared.NextDouble() * 0.3;

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 120));
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // 心跳：每 10 次轮询（约 30 秒）上报一次
        if (_heartbeatTicks++ % 10 == 0)
        {
            try { await Api.HeartbeatAsync(ct); } catch { /* 心跳失败忽略 */ }
        }

        var unread = await Api.UnreadListAsync(ct);
        var data = unread.Data;
        if (data == null) return;

        UnreadUpdated?.Invoke(data);

        foreach (var s in data.Sessions ?? new List<KefuSession>())
        {
            if (s.UnreadKefu <= 0) continue;
            if (s.Id == ActiveSessionId) continue;

            if (_notifiedLastTime.TryGetValue(s.Id, out var lastTime) && lastTime >= s.LastTime)
                continue;

            _notifiedLastTime[s.Id] = s.LastTime;
            IncomingMessage?.Invoke(s, s.LastMsg ?? "", s.LastTime);
            SoundRequested?.Invoke();
        }
    }

    /// <summary>清理某会话的提醒记录（进入会话后调用）</summary>
    public void ClearNotifyState(long sessionId) => _notifiedLastTime.Remove(sessionId);

    public void Dispose()
    {
        Stop();
        Api.Dispose();
    }
}
