using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Windowsyikefu.ViewModels;

/// <summary>
/// 头像 / 图片的统一加载入口：带缓存，并按需把相对地址（/upload/x.webp）补成绝对地址。
/// 头像和聊天图片共用同一份缓存，同一个 URL 不会重复下载。
///
/// 加载策略：后台线程把字节拉下来、完整解码并 Freeze()，再回到 UI 线程交给 Image。
/// 两个原因不能省：
/// 1) OnDemand 的位图要等渲染线程解码完才出图，在那之前 Image 会沿用上一帧画面，
///    切会话时看上去就是「先闪一下上一位访客的头像」；
/// 2) BitmapImage.UriSource 在非 UI 线程上拉网络图并不可靠，容易直接失败，
///    所以这里用 HttpClient 取字节，再走 StreamSource 解码。
/// </summary>
public static class ImageUtil
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>
    /// 正在后台加载中的地址 → 排队等这张图的所有回调。
    /// 用列表而不是简单的「占位标记」：同一张头像往往被多个地方先后请求
    /// （列表项、详情面板），它们必须都能拿到结果，否则后请求的那一处会一直空着。
    /// </summary>
    private static readonly Dictionary<string, List<Action<ImageSource>>> Pending = new();

    /// <summary>下载图片用：复用连接，别每次新建</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>相对地址解析用的站点根地址，由主窗口按配置写入</summary>
    public static string BaseUrl { get; set; } = "";

    /// <summary>
    /// 加载图片。返回 null 表示「暂时还没有」：地址为空、加载失败，或首次请求正在后台下载，
    /// 调用方应先回退到占位显示；后台完成后会在 UI 线程回调 onReady 补上图。
    /// </summary>
    public static ImageSource? Load(string? url, Action<ImageSource>? onReady = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // 缓存键用「解析后的绝对地址」而不是原始字符串：
        // 多站点下两个站点的相对路径可能一模一样（都叫 /upload/x.png），
        // 用原始串做键会让后一个站点命中前一个站点的图，也就是串图。
        var key = CacheKey(url.Trim());

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            if (Pending.TryGetValue(key, out var waiters))
            {
                // 同一张图正在下载：挂到这次请求上，下载完一起通知，不再重复发请求
                if (onReady != null) waiters.Add(onReady);
                return null;
            }

            Pending[key] = onReady != null
                ? new List<Action<ImageSource>> { onReady }
                : new List<Action<ImageSource>>();
        }

        Task.Run(async () =>
        {
            ImageSource? img = null;

            try
            {
                var uri = BuildUri(url);

                if (uri.IsAbsoluteUri &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var bytes = await Http.GetByteArrayAsync(uri).ConfigureAwait(false);
                    using var ms = new MemoryStream(bytes, writable: false);
                    img = Decode(ms);
                }
                else
                {
                    // 本地文件 / pack 资源：仍然交给 WPF 自己解析
                    img = Decode(uri);
                }
            }
            catch
            {
                img = null;   // 地址不可用：不落缓存，后面还有机会重试
            }

            List<Action<ImageSource>>? callbacks;
            lock (Cache)
            {
                Pending.Remove(key, out callbacks);
                if (img != null) Cache[key] = img;
            }

            if (img is { } ready && callbacks is { Count: > 0 })
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    // 所有等这张图的地方都在这里一次性通知到
                    foreach (var cb in callbacks) cb(ready);
                });
        });

        return null;
    }

    /// <summary>缓存键：能解析成绝对地址就用绝对地址，否则退回原始字符串</summary>
    private static string CacheKey(string url)
    {
        try
        {
            var uri = BuildUri(url);
            return uri.IsAbsoluteUri ? uri.ToString() : url;
        }
        catch
        {
            return url;
        }
    }

    /// <summary>流 → 已解码并冻结的位图。冻结后可安全交给任意线程的 Image 使用。</summary>
    private static ImageSource Decode(Stream stream)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;   // 解完再返回，调用方的流可以立刻释放
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>本地地址（file: / pack:）→ 已解码并冻结的位图</summary>
    private static ImageSource Decode(Uri uri)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = uri;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>完整 URL 原样使用，相对路径按站点根地址补全</summary>
    public static Uri BuildUri(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Uri(url);

        if (!string.IsNullOrWhiteSpace(BaseUrl) &&
            Uri.TryCreate(new Uri(BaseUrl), url, out var abs))
            return abs;

        return new Uri(url, UriKind.RelativeOrAbsolute);
    }
}
