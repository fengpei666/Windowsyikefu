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

    // ------------------------------------------------------------- 组装正文

    [Fact]
    public void 提示正文_包含当前版本与最新版本()
    {
        var info = new UpdateInfo
        {
            HasUpdate = true,
            Version = "1.1.0",
            DownloadUrl = "https://example.com/setup.exe",
            UpdateLog = "- 修复了一些问题",
        };

        var text = UpdateService.BuildMessage(info);

        Assert.Contains(UpdateService.CurrentVersion, text);
        Assert.Contains("1.1.0", text);
        Assert.Contains("修复了一些问题", text);
        Assert.DoesNotContain("强制更新", text);
    }

    [Fact]
    public void 提示正文_强制更新时多一句说明()
    {
        var info = new UpdateInfo
        {
            HasUpdate = true,
            Version = "1.1.0",
            DownloadUrl = "https://example.com/setup.exe",
            Force = true,
        };

        Assert.Contains("强制更新", UpdateService.BuildMessage(info));
    }

    [Fact]
    public void 能不能下载_要有更新且有地址()
    {
        var noUpdate = new UpdateInfo { HasUpdate = false, DownloadUrl = "https://example.com/a.exe" };
        var noUrl = new UpdateInfo { HasUpdate = true, DownloadUrl = null };
        var ok = new UpdateInfo { HasUpdate = true, DownloadUrl = "https://example.com/a.exe" };

        Assert.False(noUpdate.CanDownload);
        Assert.False(noUrl.CanDownload);
        Assert.True(ok.CanDownload);
    }
}
