using System.Threading;
using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using Windowsyikefu.Services;
using Windowsyikefu.Views;

namespace Windowsyikefu;

public partial class App : Application
{
    private const string MutexName = "Global\\WindowsYiKeFu_SingleInstance";

    private Mutex? _mutex;
    private AppConfig _config = new();
    private TrayService? _tray;

    /// <summary>每个已绑定站点一个服务实例，全部常驻后台轮询</summary>
    private readonly List<KefuService> _services = new();

    private MainWindow? _main;
    private BindWindow? _bind;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 安装脚本可用：Windowsyikefu.exe --register-protocol [--scheme=xxx]
        // 只写注册表后立即退出，不进 GUI，也不参与单实例判断（可重复执行）。
        if (e.Args.Any(a => a.Equals("--register-protocol", StringComparison.OrdinalIgnoreCase)))
        {
            var schemeArg = e.Args.FirstOrDefault(a =>
                a.StartsWith("--scheme=", StringComparison.OrdinalIgnoreCase));

            var scheme = schemeArg?["--scheme=".Length..].Trim();
            if (string.IsNullOrWhiteSpace(scheme)) scheme = DeepLinkParser.DefaultScheme;

            Shutdown(ProtocolRegistrar.EnsureRegistered(scheme) ? 0 : 1);
            return;
        }

        _mutex = new Mutex(true, MutexName, out var isFirstInstance);

        // 已有实例在跑时，先把本次启动参数（含 easykefu:// Deep Link）转交给它，
        // 由那个实例把绑定窗口提到最前，而不是再起一个进程。
        if (!isFirstInstance)
        {
            var payload = e.Args.Length > 0 ? e.Args : new[] { DeepLinkParser.ArgShow };
            if (SingleInstanceIpc.TrySendToExisting(payload))
            {
                Shutdown();
                return;
            }
        }

        // 更换绑定账号会重启程序，旧进程可能还没退干净，这里给一点重试时间
        for (var i = 0; !isFirstInstance && i < 10; i++)
        {
            Thread.Sleep(300);
            try { _mutex.Dispose(); } catch { }
            _mutex = new Mutex(true, MutexName, out isFirstInstance);
        }

        if (!isFirstInstance)
        {
            MessageBox.Show("易客服已在运行中，请在系统托盘区查看。", "易客服",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 成为首实例：开始接收后续进程转交过来的参数（Deep Link / 二次启动）
        SingleInstanceIpc.StartServer(args => Dispatcher.Invoke(() => HandleExternalArgs(args)));

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "易客服 - 未处理异常",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _config = AppConfig.Load();
        _tray = new TrayService(_config);
        _tray.OpenRequested += () => _main?.ShowFromTray();
        _tray.ExitRequested += Shutdown;
        _tray.LogoutRequested += async () =>
        {
            // 退出登录：所有站点都先让服务端把自己置为离线，再退出进程
            foreach (var svc in _services)
            {
                try { await svc.Api.LogoutAsync(); }
                catch { /* 网络不通也要保证能退出 */ }
            }

            Shutdown();
        };
        _tray.PauseChanged += paused =>
        {
            foreach (var svc in _services) svc.Paused = paused;
        };

        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        // 由通知激活而冷启动时，不弹主界面
        var launchedByToast = ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
        var startHidden = launchedByToast || e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        // 注册自定义协议（幂等）：让浏览器 / 后台绑定页能通过 easykefu:// 唤起本程序
        ProtocolRegistrar.EnsureRegistered(
            DeepLinkParser.IsValidScheme(_config.Scheme) ? _config.Scheme : DeepLinkParser.DefaultScheme);

        // 命令行里的 Deep Link（网页点「打开桌面端」时系统会把 URL 作为 argv[1] 传进来）
        DeepLinkBindInfo? deepLink = null;
        var rawLink = DeepLinkParser.FindInArgs(e.Args);
        if (rawLink != null && DeepLinkParser.TryParse(rawLink, out var parsed)) deepLink = parsed;

        if (deepLink != null)
        {
            // 协议名可能被绑定页用 ?scheme=xxx 改过，一并注册并记住
            ProtocolRegistrar.EnsureRegistered(deepLink.Scheme);
            if (!string.Equals(_config.Scheme, deepLink.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                _config.Scheme = deepLink.Scheme;
                _config.Save();
            }
        }

        if (_config.IsBound)
        {
            if (deepLink == null)
            {
                // 普通启动：--tray（开机自启）/ 通知唤醒才隐藏
                StartMainWindow(startHidden);
                return;
            }

            // 由 Deep Link 拉起：一定要把界面显示出来
            StartMainWindow(startHidden: false);

            if (MatchesBoundConfig(deepLink))
            {
                // 链接指向的就是当前账号：不必重新绑定，激活主界面并立刻调一次接口，
                // 让后台绑定页立刻显示「已连接 + IP」。
                _main?.ShowFromTray();
                foreach (var svc in _services) _ = svc.Api.PingAsync();
                return;
            }

            // 链接是另一个账号：走重新绑定
            ShowBindWindow(deepLink);
        }
        else
        {
            ShowBindWindow(deepLink);
        }
    }

    private void StartMainWindow(bool startHidden)
    {
        _config = AppConfig.Load();

        // 托盘要跟着换成同一份配置实例：否则它保存开关时会用旧的站点列表覆盖磁盘
        _tray?.UpdateConfig(_config);

        // 每个绑定站点起一个服务：它们各自轮询，后台站点照样收消息、弹通知
        foreach (var svc in _services) svc.Dispose();
        _services.Clear();
        foreach (var site in _config.Sites.Where(s => s.IsBound))
            _services.Add(new KefuService(site, _config));

        if (_services.Count == 0) return;

        _main = new MainWindow(_services, _config, _tray!);

        // 启动参数含 --tray（开机自启）时只驻留托盘后台收消息，不弹出界面
        if (!startHidden) _main.Show();

        foreach (var svc in _services) svc.Start();

        // 立刻上报一次心跳：后台绑定页靠「该时间点之后客户端调用过接口」判断是否已连接，
        // 不马上调的话页面要等好几分钟才变绿。
        _ = Task.Run(async () =>
        {
            foreach (var svc in _services)
            {
                try { await svc.Api.HeartbeatAsync(); } catch { /* 网络不通时由轮询循环重试 */ }
            }
        });
    }

    // ------------------------------------------------------ Deep Link 处理

    /// <summary>外部进程转交过来的参数（Deep Link 或二次启动）</summary>
    private void HandleExternalArgs(string[] args)
    {
        try
        {
            // 用户重复启动程序：把已有窗口提到最前即可
            if (args.Contains(DeepLinkParser.ArgShow, StringComparer.OrdinalIgnoreCase))
            {
                if (_bind is { IsLoaded: true }) WindowActivation.BringToFront(_bind);
                else _main?.ShowFromTray();
                return;
            }

            var rawLink = DeepLinkParser.FindInArgs(args);
            if (rawLink == null || !DeepLinkParser.TryParse(rawLink, out var deepLink)) return;

            ProtocolRegistrar.EnsureRegistered(deepLink.Scheme);
            if (!string.Equals(_config.Scheme, deepLink.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                _config.Scheme = deepLink.Scheme;
                _config.Save();
            }

            // 已绑定且链接就是当前账号：只激活主界面 + 立刻调一次接口
            if (_main != null && MatchesBoundConfig(deepLink))
            {
                _main.ShowFromTray();
                foreach (var svc in _services) _ = svc.Api.PingAsync();
                return;
            }

            ShowBindWindow(deepLink);
        }
        catch { /* 参数异常不影响已运行的程序 */ }
    }

    /// <summary>打开（或复用）绑定窗口，deepLink 非空时预填凭证并自动绑定，且把窗口提到最前</summary>
    private void ShowBindWindow(DeepLinkBindInfo? deepLink)
    {
        if (_bind is { IsLoaded: true })
        {
            if (deepLink != null) _bind.ApplyDeepLink(deepLink);
            else WindowActivation.BringToFront(_bind);
            return;
        }

        _bind = new BindWindow(_config, deepLink);
        var bound = _bind.ShowDialog() == true;
        _bind = null;

        if (bound)
        {
            _config = AppConfig.Load();

            if (_main == null)
            {
                StartMainWindow(startHidden: false);
                _main?.ShowFromTray();
            }
            else
            {
                // 运行中重新绑定：重启进程让新凭证生效
                Restart();
            }
        }
        else if (_main == null)
        {
            Shutdown();
        }
    }

    /// <summary>Deep Link 里的凭证是否已经绑定过（接口地址忽略末尾斜杠差异）</summary>
    private bool MatchesBoundConfig(DeepLinkBindInfo deepLink)
    {
        if (!deepLink.HasCredentials) return false;

        return _config.Sites.Any(s =>
            SiteConfig.SameUrl(deepLink.ApiUrl, s.ApiUrl) &&
            SiteConfig.SameUrl(deepLink.AppId, s.AppId) &&
            string.Equals(deepLink.AppSecret, s.AppSecret, StringComparison.Ordinal));
    }

    // ------------------------------------------------------ 通知激活处理

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        ToastArguments args;
        try { args = ToastArguments.Parse(e.Argument); }
        catch { return; }

        var action = args.TryGetValue(ToastService.ArgAction, out var a) ? a : ToastService.ActionOpen;
        if (!long.TryParse(args.TryGetValue(ToastService.ArgSessionId, out var sid) ? sid : "0", out var sessionId)
            || sessionId <= 0)
        {
            return;
        }

        // 通知里带着站点标识：回复 / 打开都要落到消息所属的那个站点上
        var siteKey = args.TryGetValue(ToastService.ArgSiteKey, out var key) ? key : null;
        var site = ServiceByKey(siteKey);

        if (action == ToastService.ActionReply)
        {
            var text = "";
            if (e.UserInput != null && e.UserInput.TryGetValue(ToastService.ReplyBoxId, out var input))
                text = (Convert.ToString(input) ?? "").Trim();

            if (text.Length == 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    // 站点已被移除 / 标识对不上：宁可什么都不做，也不能把回复发到别的站点去
                    if (string.IsNullOrWhiteSpace(siteKey) || site == null) return;

                    await site.Api.SendMessageAsync(sessionId, text);
                    Dispatcher.Invoke(() => _main?.NotifyReplySent(sessionId));
                }
                catch
                {
                    Dispatcher.Invoke(() =>
                        _tray?.ShowBalloon("快捷回复失败", "网络异常，请在主界面中重试。"));
                }
            });

            return;
        }

        Dispatcher.Invoke(() => _main?.ShowFromTray(siteKey, sessionId));
    }

    /// <summary>
    /// 按站点标识取服务实例。找不到就返回 null——绝不能退回第一个站点，
    /// 否则会把消息回复 / 打开会话错投到另一个站点的同名会话上。
    /// </summary>
    private KefuService? ServiceByKey(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? _services.FirstOrDefault()
            : _services.FirstOrDefault(s => s.Site.Key == key);

    /// <summary>重新启动客户端（更换绑定账号后回到绑定向导）</summary>
    public static void Restart()
    {
        if (Current is App app)
        {
            // 关键：先拆掉单实例通道并释放互斥体。
            // 否则新进程会把「无参数启动」当成"已有实例在跑"转发过来，自己直接退出，导致起不来。
            SingleInstanceIpc.Stop();
            try { app._mutex?.ReleaseMutex(); } catch { }
            try { app._mutex?.Dispose(); } catch { }
            app._mutex = null;

            Thread.Sleep(200); // 给旧实例的监听一点退出时间
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                System.Diagnostics.Process.Start(exe);
        }
        catch { /* 启动失败时至少让当前进程干净退出 */ }

        if (Current is App a) a._main?.ForceClose();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
            SingleInstanceIpc.Stop();
            foreach (var svc in _services) svc.Dispose();
            _tray?.Dispose();
            ToastService.Uninstall();
            _mutex?.ReleaseMutex();
        }
        catch { }

        base.OnExit(e);
    }
}
