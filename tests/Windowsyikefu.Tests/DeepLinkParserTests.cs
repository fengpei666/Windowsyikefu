using Windowsyikefu.Services;
using Xunit;

namespace Windowsyikefu.Tests;

/// <summary>
/// Deep Link 解析测试：商城后台「打开桌面端」拼的链接必须能被稳定解析，
/// 这里覆盖各种形态、别名与非法输入，防止以后改动把绑定流程弄坏。
/// </summary>
public class DeepLinkParserTests
{
    // ------------------------------------------------ 正常解析

    [Fact]
    public void 标准链接_解析出完整凭证()
    {
        const string raw = "easykefu://bind?api_url=https://example.com/api.php&app_id=id123&app_secret=sec456";

        Assert.True(DeepLinkParser.TryParse(raw, out var info));
        Assert.Equal("https://example.com/api.php", info.ApiUrl);
        Assert.Equal("id123", info.AppId);
        Assert.Equal("sec456", info.AppSecret);
        Assert.Equal("easykefu", info.Scheme);
        Assert.True(info.HasCredentials);
        Assert.True(info.Auto);
        Assert.Equal(raw, info.RawUrl);
    }

    [Fact]
    public void URL编码的值_能被正确解码_含特殊字符也不截断()
    {
        // api_url 里有 ?a=1&b=2，编码后 & 变成 %26，才不会被参数分隔符切开
        const string raw = "easykefu://bind?api_url=https%3A%2F%2Fexample.com%2Fapi.php%3Fa%3D1%26b%3D2" +
                           "&app_id=x1&app_secret=s1";

        Assert.True(DeepLinkParser.TryParse(raw, out var info));
        Assert.Equal("https://example.com/api.php?a=1&b=2", info.ApiUrl);
        Assert.Equal("x1", info.AppId);
        Assert.Equal("s1", info.AppSecret);
    }

    [Fact]
    public void 协议后无路径_仅有query_同样能解析()
    {
        const string raw = "easykefu://?api_url=https://a.com/api&app_id=a&app_secret=b";

        Assert.True(DeepLinkParser.TryParse(raw, out var info));
        Assert.Equal("https://a.com/api", info.ApiUrl);
        Assert.Equal("a", info.AppId);
        Assert.Equal("b", info.AppSecret);
    }

    [Fact]
    public void 纯query串_没有协议前缀_也能解析()
    {
        // 值里的 "https://" 也含 ://，但它不是合法协议名，不能被误判成协议链接整串丢掉
        const string raw = "api_url=https://a.com/api&app_id=a&app_secret=b";

        Assert.True(DeepLinkParser.TryParse(raw, out var info));
        Assert.Equal("https://a.com/api", info.ApiUrl);
        Assert.Equal("easykefu", info.Scheme); // 没写协议就用默认值
    }

    [Fact]
    public void 参数别名_与规范名等价()
    {
        const string raw = "easykefu://bind?url=https://a.com/api&appid=a&secret=b";

        Assert.True(DeepLinkParser.TryParse(raw, out var info));
        Assert.Equal("https://a.com/api", info.ApiUrl);
        Assert.Equal("a", info.AppId);
        Assert.Equal("b", info.AppSecret);
        Assert.True(info.HasCredentials);
    }

    [Fact]
    public void scheme参数_可覆盖默认协议名()
    {
        const string raw = "mykefu://bind?api_url=https://a.com&app_id=a&app_secret=b";

        Assert.True(DeepLinkParser.TryParse(raw, out var info));
        Assert.Equal("mykefu", info.Scheme);

        // 也可以用 ?scheme= 显式指定
        Assert.True(DeepLinkParser.TryParse("easykefu://bind?api_url=https://a.com&scheme=other", out var info2));
        Assert.Equal("other", info2.Scheme);
    }

    [Fact]
    public void auto为0_表示不自动拉起()
    {
        Assert.True(DeepLinkParser.TryParse(
            "easykefu://bind?api_url=https://a.com&auto=0", out var info));
        Assert.False(info.Auto);

        Assert.True(DeepLinkParser.TryParse(
            "easykefu://bind?api_url=https://a.com&auto=1", out var info2));
        Assert.True(info2.Auto);
    }

    [Fact]
    public void download参数_被记录()
    {
        Assert.True(DeepLinkParser.TryParse(
            "easykefu://bind?api_url=https://a.com&download=https%3A%2F%2Fa.com%2Fsetup.exe", out var info));
        Assert.Equal("https://a.com/setup.exe", info.DownloadUrl);
    }

    [Fact]
    public void 凭证不完整_HasCredentials为false_但HasAny为true()
    {
        Assert.True(DeepLinkParser.TryParse("easykefu://bind?api_url=https://a.com&app_id=only-id", out var info));
        Assert.False(info.HasCredentials);
        Assert.True(info.HasAny);
    }

    [Fact]
    public void 含fragment_不影响解析()
    {
        Assert.True(DeepLinkParser.TryParse(
            "easykefu://bind?api_url=https://a.com/api&app_id=a#section", out var info));
        Assert.Equal("https://a.com/api", info.ApiUrl);
        Assert.Equal("a", info.AppId);
    }

    [Fact]
    public void 末尾带引号_能被清理()
    {
        Assert.True(DeepLinkParser.TryParse("\"easykefu://bind?api_url=https://a.com\"", out var info));
        Assert.Equal("https://a.com", info.ApiUrl);
    }

    // ------------------------------------------------ 非法输入

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("just some text")]
    [InlineData("easykefu://bind")]          // 有协议但完全没有参数
    public void 非法输入_返回false(string? raw)
    {
        Assert.False(DeepLinkParser.TryParse(raw, out _));
    }

    [Fact]
    public void 非法百分号序列_不抛异常()
    {
        var ex = Record.Exception(() =>
        {
            Assert.True(DeepLinkParser.TryParse("easykefu://bind?api_url=100%&app_id=a", out var info));
            Assert.Equal("100%", info.ApiUrl); // 解不开就按原文保留
        });
        Assert.Null(ex);
    }

    // ------------------------------------------------ 命令行参数查找

    [Fact]
    public void FindInArgs_跳过开关参数_找到链接()
    {
        var args = new[] { "--tray", "--show", "easykefu://bind?api_url=https://a.com" };
        Assert.Equal("easykefu://bind?api_url=https://a.com", DeepLinkParser.FindInArgs(args));
    }

    [Fact]
    public void FindInArgs_找到纯query串()
    {
        var args = new[] { "C:\\some\\path.exe", "api_url=https://a.com&app_id=x" };
        Assert.Equal("api_url=https://a.com&app_id=x", DeepLinkParser.FindInArgs(args));
    }

    [Fact]
    public void FindInArgs_没有链接时返回null()
    {
        Assert.Null(DeepLinkParser.FindInArgs(new[] { "--tray", "--show" }));
        Assert.Null(DeepLinkParser.FindInArgs(Array.Empty<string>()));
        Assert.Null(DeepLinkParser.FindInArgs(null));
    }

    // ------------------------------------------------ 协议名校验

    [Theory]
    [InlineData("easykefu", true)]
    [InlineData("a", true)]
    [InlineData("my-kefu.v2+test", true)]
    [InlineData("1abc", false)]      // 数字开头
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("a/b", false)]
    public void IsValidScheme_边界校验(string scheme, bool expected)
    {
        Assert.Equal(expected, DeepLinkParser.IsValidScheme(scheme));
    }

    [Fact]
    public void IsValidScheme_超长协议名被拒()
    {
        Assert.False(DeepLinkParser.IsValidScheme(new string('a', 41)));
        Assert.True(DeepLinkParser.IsValidScheme(new string('a', 40)));
    }
}
