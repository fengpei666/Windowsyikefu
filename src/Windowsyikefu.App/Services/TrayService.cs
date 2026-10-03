using System.IO;
using System.Windows;
using WF = System.Windows.Forms;

namespace Windowsyikefu.Services;

/// <summary>系统托盘常驻图标：关闭窗口后程序仍留在托盘继续收消息。</summary>
public sealed class TrayService : IDisposable
{
    private AppConfig _config;
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _pauseItem;
    private readonly WF.ToolStripMenuItem _closeToTrayItem;
    private readonly WF.ToolStripMenuItem _autoStartItem;
    private readonly WF.ToolStripMenuItem _toastItem;
    private readonly WF.ToolStripMenuItem _soundItem;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action? LogoutRequested;
    public event Action<bool>? PauseChanged;
    public event Action? SettingsChanged;

    private bool _paused;

    public TrayService(AppConfig config)
    {
        _config = config;

        _pauseItem = new WF.ToolStripMenuItem("暂停接收消息");
        _pauseItem.Click += (_, _) =>
        {
            _paused = !_paused;
            _pauseItem.Checked = _paused;
            _pauseItem.Text = _paused ? "继续接收消息" : "暂停接收消息";
            UpdateTooltip();
            PauseChanged?.Invoke(_paused);
        };

        _closeToTrayItem = new WF.ToolStripMenuItem("关闭窗口时最小化到托盘")
        {
            Checked = config.CloseToTray
        };
        _closeToTrayItem.Click += (_, _) =>
        {
            _closeToTrayItem.Checked = !_closeToTrayItem.Checked;
            config.CloseToTray = _closeToTrayItem.Checked;
            config.Save();
            SettingsChanged?.Invoke();
        };

        _autoStartItem = new WF.ToolStripMenuItem("开机自动启动")
        {
            Checked = AutoStartManager.IsEnabled()
        };
        _autoStartItem.Click += (_, _) =>
        {
            _autoStartItem.Checked = !_autoStartItem.Checked;
            AutoStartManager.Set(_autoStartItem.Checked);
            SettingsChanged?.Invoke();
        };

        _toastItem = new WF.ToolStripMenuItem("弹出系统通知")
        {
            Checked = config.ToastEnabled
        };
        _toastItem.Click += (_, _) =>
        {
            _toastItem.Checked = !_toastItem.Checked;
            config.ToastEnabled = _toastItem.Checked;
            config.Save();
            SettingsChanged?.Invoke();
        };

        _soundItem = new WF.ToolStripMenuItem("消息提示音")
        {
            Checked = config.SoundEnabled
        };
        _soundItem.Click += (_, _) =>
        {
            _soundItem.Checked = !_soundItem.Checked;
            config.SoundEnabled = _soundItem.Checked;
            config.Save();
            SettingsChanged?.Invoke();
        };

        var menu = new WF.ContextMenuStrip();
        var open = new WF.ToolStripMenuItem("打开主界面");
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => OpenRequested?.Invoke();

        var logout = new WF.ToolStripMenuItem("退出登录（置为离线）");
        logout.Click += (_, _) => LogoutRequested?.Invoke();

        var exit = new WF.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(open);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(_toastItem);
        menu.Items.Add(_soundItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(_closeToTrayItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(logout);
        menu.Items.Add(exit);

        _icon = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "易客服",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public bool CloseToTray => _closeToTrayItem.Checked;

    public bool IsPaused => _paused;

    /// <summary>设置窗口切换「暂停接收」时调用，同步托盘菜单外观</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;

        _paused = paused;
        _pauseItem.Checked = paused;
        _pauseItem.Text = paused ? "继续接收消息" : "暂停接收消息";
        UpdateTooltip();
        PauseChanged?.Invoke(paused);
    }

    /// <summary>
    /// 配置实例被替换时（主窗口启动 / 绑定窗口关闭后重新 Load）同步过来。
    /// 托盘的开关项会直接 Save() 整份配置，如果还拿着旧实例，
    /// 就会用旧的站点列表把刚添加 / 移除的站点覆盖回去。
    /// </summary>
    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        SyncFromConfig();
    }

    /// <summary>设置窗口保存后，把托盘菜单勾选状态同步为最新配置</summary>
    public void SyncFromConfig()
    {
        _toastItem.Checked = _config.ToastEnabled;
        _soundItem.Checked = _config.SoundEnabled;
        _closeToTrayItem.Checked = _config.CloseToTray;
        _autoStartItem.Checked = AutoStartManager.IsEnabled();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("Assets/tray.ico", UriKind.Relative));
            if (info?.Stream != null)
            {
                using var s = info.Stream;
                return new System.Drawing.Icon(s, new System.Drawing.Size(32, 32));
            }
        }
        catch { }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>更新托盘提示文字（例如显示未读数）</summary>
    public void SetUnread(int unreadTotal)
    {
        if (unreadTotal > 0)
            _icon.Text = $"易客服 - {unreadTotal} 条未读" + (_paused ? "（已暂停）" : "");
        else
            UpdateTooltip();
    }

    private void UpdateTooltip() => _icon.Text = "易客服" + (_paused ? "（已暂停接收）" : "");

    /// <summary>气泡提示（系统关闭 Toast 时的兜底）</summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(5000);
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { }
    }
}
