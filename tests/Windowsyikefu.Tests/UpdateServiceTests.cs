using Windowsyikefu.Services;
using Xunit;

namespace Windowsyikefu.Tests;

/// <summary>
/// 更新检查的解析规则测试。
/// tag 规整错了会算错版本号、误报「已是最新」，用户就永远收不到升级提示，
/// 所以这里把各种 tag 写法和强制更新标记都钉死。
/// </summary>
public class UpdateServiceTests
{
    // ------------------------------------------------------------- tag 规整

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  v1.2.3  ", "1.2.3")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("v1.2.3-force", "1.2.3")]
    public void Tag_常见写法_规整成三段式(string tag, string expected)
    {
        Assert.Equal(expected, UpdateService.NormalizeTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("release")]
    [InlineData("v")]
    [InlineData("v-")]
    public void Tag_不是版本号_返回空(string? tag)
    {
        Assert.Null(UpdateService.NormalizeTag(tag));
    }

    // ------------------------------------------------------------- 版本比较

    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("1.9.9", "2.0.0", false)]
    public void 版本比较_远端更新才算有更新(string remote, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerVersion(remote, current));
    }

    [Theory]
    [InlineData("abc", "1.0.0")]
    [InlineData("1.0.0", "abc")]
    public void 版本比较_任一方无法解析_不算有更新(string remote, string current)
    {
        Assert.False(UpdateService.IsNewerVersion(remote, current));
    }

    // ------------------------------------------------------------- 强制更新标记

    [Theory]
    [InlineData("[强制] v1.2.0", "v1.2.0")]   // 标题前缀
    [InlineData("[强制]v1.2.0", "v1.2.0")]    // 前缀后不带空格
    [InlineData("【强制】v1.2.0", "v1.2.0")]   // 中文方括号
    [InlineData("v1.2.0", "v1.2.0-force")]    // tag 后缀
    [InlineData("v1.2.0", "v1.2.0-FORCE")]    // 后缀不区分大小写
    public void 强制更新_标记写法(string? name, string? tag)
    {
        Assert.True(UpdateService.IsForceRelease(name, tag));
    }

    [Theory]
    [InlineData("v1.2.0", "v1.2.0")]
    [InlineData(null, "v1.2.0")]
    [InlineData("v1.2.0 修复强制更新后的闪退问题", "v1.2.0")]  // 正文里提到不算
    [InlineData("v1.2.0", "v1.2.0-forced")]
    public void 强制更新_普通发版(string? name, string? tag)
    {
        Assert.False(UpdateService.IsForceRelease(name, tag));
    }

    // ------------------------------------------------------------- 更新说明排版

    /// <summary>旧 Release 正文是下载说明表格，表格行不该跑到弹窗里变成一堆竖线</summary>
    [Fact]
    public void 更新说明_表格与分隔线被丢弃()
    {
        var log = """
            ## 易客服 Windows 客户端

            | 下载哪个 | 说明 |
            | --- | --- |
            | `kefu_setup.exe` | 安装包 |
            """;

        var lines = UpdateService.ParseLog(log);

        Assert.Single(lines);
        Assert.Equal(UpdateLogKind.Heading, lines[0].Kind);
        Assert.Equal("易客服 Windows 客户端", lines[0].Text);
    }

    [Fact]
    public void 更新说明_标题条目段落分开渲染()
    {
        var log = """
            ### 变更
            - 更新源迁移
            * 修了闪退
            普通说明
            """;

        var lines = UpdateService.ParseLog(log);

        Assert.Equal(4, lines.Count);
        Assert.Equal(new UpdateLogLine(UpdateLogKind.Heading, "变更"), lines[0]);
        Assert.Equal(new UpdateLogLine(UpdateLogKind.Bullet, "更新源迁移"), lines[1]);
        Assert.Equal(new UpdateLogLine(UpdateLogKind.Bullet, "修了闪退"), lines[2]);
        Assert.Equal(new UpdateLogLine(UpdateLogKind.Paragraph, "普通说明"), lines[3]);
    }

    [Fact]
    public void 更新说明_行内记号被清理()
    {
        var log = "- 修复 **强制更新** 后 `1.0.0` 的 [闪退](https://example.com/issue/1)";

        var lines = UpdateService.ParseLog(log);

        Assert.Equal("修复 强制更新 后 1.0.0 的 闪退", lines[0].Text);
    }

    [Fact]
    public void 更新说明_引用块去掉箭头()
    {
        Assert.Equal("只在紧急时使用", UpdateService.ParseLog("> 只在紧急时使用")[0].Text);
    }

    [Fact]
    public void 更新说明_空内容返回空列表()
    {
        Assert.Empty(UpdateService.ParseLog(null));
        Assert.Empty(UpdateService.ParseLog("   "));
        Assert.Empty(UpdateService.ParseLog("---"));
        Assert.Empty(UpdateService.ParseLog("***"));
    }

    [Fact]
    public void 能不能下载_要有更新且有地址()
    {
        var noUpdate = new UpdateInfo { HasUpdate = false, SetupUrl = "https://example.com/a.exe" };
        var noUrl = new UpdateInfo { HasUpdate = true };
        var ok = new UpdateInfo { HasUpdate = true, SetupUrl = "https://example.com/a.exe" };

        Assert.False(noUpdate.CanDownload);
        Assert.False(noUrl.CanDownload);
        Assert.True(ok.CanDownload);
    }

    // ------------------------------------------------------------- 下载进度

    [Fact]
    public void 下载进度_按总长度算百分比()
    {
        var half = new DownloadProgress(50, 100);

        Assert.True(half.HasTotal);
        Assert.Equal(50, half.Percent);
        Assert.Equal(0, new DownloadProgress(0, 100).Percent);
    }

    [Theory]
    [InlineData(100, 0)]    // 服务端没给 Content-Length
    [InlineData(100, -1)]   // 同上，约定用 -1 表示
    public void 下载进度_不知道总长度时按0处理(long received, long total)
    {
        var progress = new DownloadProgress(received, total);

        Assert.False(progress.HasTotal);
        Assert.Equal(0, progress.Percent);
    }

    [Fact]
    public void 下载进度_超出总长度不会超过100()
    {
        Assert.Equal(100, new DownloadProgress(120, 100).Percent);
    }

    // ------------------------------------------------------------- 下载地址选择

    private static UpdateInfo FullInfo() => new()
    {
        HasUpdate = true,
        Version = "1.1.0",
        SetupUrl = "https://example.com/setup.exe",
        PortableUrl = "https://example.com/app.exe",
        PageUrl = "https://example.com/page",
    };

    [Fact]
    public void 下载地址_安装版优先安装包()
    {
        Assert.Equal("https://example.com/setup.exe", FullInfo().ResolveUrl(installed: true));
    }

    [Fact]
    public void 下载地址_免安装版优先单文件()
    {
        Assert.Equal("https://example.com/app.exe", FullInfo().ResolveUrl(installed: false));
    }

    [Fact]
    public void 下载地址_缺一个时互相兜底()
    {
        var onlySetup = new UpdateInfo { SetupUrl = "https://example.com/setup.exe" };
        var onlyPortable = new UpdateInfo { PortableUrl = "https://example.com/app.exe" };
        var onlyPage = new UpdateInfo { PageUrl = "https://example.com/page" };

        Assert.Equal("https://example.com/setup.exe", onlySetup.ResolveUrl(installed: false));
        Assert.Equal("https://example.com/app.exe", onlyPortable.ResolveUrl(installed: true));
        Assert.Equal("https://example.com/page", onlyPage.ResolveUrl(installed: true));
        Assert.Null(new UpdateInfo().ResolveUrl(installed: true));
    }
}
