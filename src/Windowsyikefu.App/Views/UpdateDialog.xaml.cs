using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Windowsyikefu.Services;

namespace Windowsyikefu.Views;

/// <summary>
/// 更新提示窗口：与设置窗口同一套无边框材质，把「版本对比 + 更新内容 + 下载」放在一起，
/// 不再借用系统的 MessageBox。
///
/// 普通更新可以「稍后再说」；强制更新只留「立即下载」，并且关不掉 ——
/// 必须把下载页打开才算完成。
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;

    /// <summary>窗口走到了哪一步，决定内容区和按钮怎么显示</summary>
    private enum Stage { Idle, Downloading, Installing, Failed }

    private Stage _stage = Stage.Idle;

    /// <summary>是否已经交给安装程序：决定强制更新时允不允许关窗</summary>
    private bool _downloaded;

    /// <summary>取消下载用，每次重新下载换一个新的</summary>
    private CancellationTokenSource? _cts;

    /// <summary>下载好的更新包本地路径</summary>
    private string? _packagePath;

    public UpdateDialog(UpdateInfo info)
    {
        _info = info;

        InitializeComponent();

        TxtTitle.Text = info.Force ? $"需要更新到 {info.Version}" : $"发现新版本 {info.Version}";
        TxtCurrent.Text = UpdateService.CurrentVersion;
        TxtLatest.Text = info.Version ?? "未知";

        if (info.Force)
        {
            BoxForce.Visibility = Visibility.Visible;
            BtnLater.Visibility = Visibility.Collapsed;
            BtnClose.Visibility = Visibility.Collapsed;

            BtnDownload.Content = "立即下载并退出";
            BtnDownload.Width = 128;
            TxtHint.Text = "下载安装后请重新打开本程序";
        }
        else
        {
            TxtHint.Text = "下载后双击安装即可，无需先卸载旧版本";
        }

        RenderLog(info.UpdateLog);

        // 先透明，等内容渲染完再淡入，避免窗口"啪"地跳出来
        Opacity = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与主窗口 / 设置窗口同一套材质：能开毛玻璃就用透明底，开不成退回不透明浅灰
        WindowEffects.ApplyBackdrop(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });

        // 轻微上移，视觉上"推"出来而不是弹出来
        var slide = new TranslateTransform(0, 10);
        Root.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 强制更新没有「稍后再说」：没点下载就不许关，否则等于绕过了更新
        if (_info.Force && !_downloaded)
        {
            e.Cancel = true;
            return;
        }

        // 关窗口顺手停掉还在跑的下载，别让它继续占带宽
        _cts?.Cancel();

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Dispose();
        _cts = null;

        base.OnClosed(e);
    }

    // ------------------------------------------------------------- 内容排版

    /// <summary>
    /// 把 Release 正文按行渲染：小节标题加粗、条目带圆点、其余当段落。
    /// 表格、分隔线这类 markdown 装饰在纯文本窗口里没有意义，解析时就丢掉了。
    /// </summary>
    private void RenderLog(string? log)
    {
        var lines = UpdateService.ParseLog(log);

        if (lines.Count == 0)
        {
            LogBody.Children.Add(new TextBlock
            {
                Text = "这个版本没有附带更新说明。",
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            });
            return;
        }

        foreach (var line in lines)
            LogBody.Children.Add(BuildLine(line));
    }

    private UIElement BuildLine(UpdateLogLine line)
    {
        var primary = (Brush)FindResource("TextPrimaryBrush");
        var brand = (Brush)FindResource("BrandBrush");

        switch (line.Kind)
        {
            case UpdateLogKind.Heading:
                return new TextBlock
                {
                    Text = line.Text,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = primary,
                    TextWrapping = TextWrapping.Wrap,
                    // 第一个小节标题不需要再往上留白
                    Margin = new Thickness(0, LogBody.Children.Count == 0 ? 0 : 14, 0, 7),
                };

            case UpdateLogKind.Bullet:
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new Border
                {
                    Width = 4,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = brand,
                    Opacity = 0.75,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 8, 0, 0),
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var text = new TextBlock
                {
                    Text = line.Text,
                    FontSize = 12.5,
                    LineHeight = 20,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = primary,
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);

                return row;

            default:
                return new TextBlock
                {
                    Text = line.Text,
                    FontSize = 12.5,
                    LineHeight = 20,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = primary,
                    Margin = new Thickness(0, 0, 0, 6),
                };
        }
    }

    // ------------------------------------------------------------- 按钮

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        // 下载中这个按钮就是「取消」
        if (_stage == Stage.Downloading)
        {
            _cts?.Cancel();
            return;
        }

        await DownloadAndInstallAsync();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        // 失败后这个按钮变成「用浏览器下载」，点了就走老路
        if (_stage == Stage.Failed)
        {
            var url = _info.ResolveUrl(UpdateService.IsInstalled);
            if (!string.IsNullOrWhiteSpace(url)) UpdateService.OpenDownload(url);
            return;
        }

        DialogResult = false;
    }

    // ------------------------------------------------------------- 在线更新

    /// <summary>
    /// 应用内下载 → 静默安装 → 自动重启。全程不离开这个窗口，
    /// 用户只需要等进度条走完，不用去浏览器里翻下载目录。
    /// </summary>
    private async Task DownloadAndInstallAsync()
    {
        var url = _info.ResolveUrl(UpdateService.IsInstalled);
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowFailed("没有拿到下载地址");
            return;
        }

        _stage = Stage.Downloading;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        PanelLog.Visibility = Visibility.Collapsed;
        PanelProgress.Visibility = Visibility.Visible;
        BarProgress.Value = 0;
        TxtProgress.Text = "正在下载新版本…";
        TxtProgressDetail.Text = "正在建立连接";

        BtnDownload.Content = "取消";
        BtnLater.Visibility = Visibility.Collapsed;
        TxtHint.Text = "下载完成后会自动安装并重新启动";

        var progress = new Progress<DownloadProgress>(p =>
        {
            BarProgress.Value = p.Percent;
            TxtProgressDetail.Text = p.HasTotal
                ? $"{Mb(p.Received)} / {Mb(p.Total)} MB"
                : $"{Mb(p.Received)} MB";
        });

        try
        {
            _packagePath = await UpdateService.DownloadPackageAsync(url, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            ResetToIdle();
            return;
        }
        catch
        {
            ShowFailed("下载失败，请检查网络后重试");
            return;
        }

        await InstallAsync();
    }

    private async Task InstallAsync()
    {
        _stage = Stage.Installing;

        BarProgress.Value = 100;
        TxtProgress.Text = "下载完成，正在安装…";
        TxtProgressDetail.Text = "安装程序会自动关闭本程序，装好后重新打开";
        TxtHint.Text = "安装过程中请不要重复启动程序";

        BtnDownload.Visibility = Visibility.Collapsed;

        // 留一点时间让人看清这行字，太快反而像闪退
        await Task.Delay(800);

        if (_packagePath == null || !UpdateService.LaunchUpdate(_packagePath))
        {
            ShowFailed("安装程序没能启动，请改用浏览器下载");
            return;
        }

        _downloaded = true;
        DialogResult = true;
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0");

    private void ShowFailed(string reason)
    {
        _stage = Stage.Failed;

        PanelLog.Visibility = Visibility.Collapsed;
        PanelProgress.Visibility = Visibility.Visible;
        BarProgress.Value = 0;
        TxtProgress.Text = "更新没能完成";
        TxtProgressDetail.Text = reason;

        BtnDownload.Visibility = Visibility.Visible;
        BtnDownload.Content = "重试";
        BtnLater.Content = "用浏览器下载";
        BtnLater.Visibility = Visibility.Visible;
        TxtHint.Text = "也可以手动下载安装包";
    }

    private void ResetToIdle()
    {
        _stage = Stage.Idle;

        PanelLog.Visibility = Visibility.Visible;
        PanelProgress.Visibility = Visibility.Collapsed;

        BtnDownload.Visibility = Visibility.Visible;
        BtnDownload.Content = "立即下载";
        BtnLater.Content = "稍后再说";
        BtnLater.Visibility = _info.Force ? Visibility.Collapsed : Visibility.Visible;

        TxtHint.Text = _info.Force
            ? "下载安装后请重新打开本程序"
            : "下载后双击安装即可，无需先卸载旧版本";
    }
}
