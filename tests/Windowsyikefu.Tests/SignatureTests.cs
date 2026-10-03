using Windowsyikefu.Services;
using Xunit;

namespace Windowsyikefu.Tests;

/// <summary>
/// 接口签名测试：签名规则是「除 sign 外所有参数按 key 升序，拼 k=v&amp;... 后取
/// HMAC-SHA256 小写十六进制」。算错会导致服务端拒绝所有请求，所以必须有基准断言。
/// </summary>
public class SignatureTests
{
    /// <summary>基准值用独立的 HMAC-SHA256 实现预先算出，避免「自己测自己」</summary>
    private const string Secret1 = "test-secret";
    private const string Expected1 = "bef9db7317f1d1d2f647713fc91601344d0986a247c9fdae0195acd549b3f91e";

    private const string Secret2 = "my-secret-key";
    private const string Expected2 = "a4012486bebd18aeb2748a2843fd159e262826449020e1c847a334ced4e578aa";

    private static string Sign(IDictionary<string, string> parameters, string secret)
    {
        using var client = new KefuApiClient("https://example.com/api.php", "test-app", secret);
        return client.Sign(parameters);
    }

    [Fact]
    public void 签名_已知输入_与基准值一致()
    {
        var p = new Dictionary<string, string>
        {
            ["app_id"] = "abc",
            ["nonce"] = "123",
            ["timestamp"] = "1700000000",
        };

        Assert.Equal(Expected1, Sign(p, Secret1));
    }

    [Fact]
    public void 签名_含会话参数的整串_与基准值一致()
    {
        var p = new Dictionary<string, string>
        {
            ["app_id"] = "e689ffa25d0814a01d02695efc6e042b",
            ["session_id"] = "246",
            ["timestamp"] = "1700000000",
        };

        Assert.Equal(Expected2, Sign(p, Secret2));
    }

    [Fact]
    public void 签名_与参数插入顺序无关()
    {
        var a = new Dictionary<string, string>
        {
            ["app_id"] = "abc",
            ["nonce"] = "123",
            ["timestamp"] = "1700000000",
        };
        var b = new Dictionary<string, string>
        {
            ["timestamp"] = "1700000000",
            ["app_id"] = "abc",
            ["nonce"] = "123",
        };

        Assert.Equal(Sign(a, Secret1), Sign(b, Secret1));
    }

    [Fact]
    public void 签名_sign参数本身不参与计算()
    {
        var without = new Dictionary<string, string>
        {
            ["app_id"] = "abc",
            ["timestamp"] = "1700000000",
        };
        var with = new Dictionary<string, string>
        {
            ["app_id"] = "abc",
            ["timestamp"] = "1700000000",
            ["sign"] = "should-be-ignored",
        };

        Assert.Equal(Sign(without, Secret1), Sign(with, Secret1));
    }

    [Fact]
    public void 签名_空值参数照样参与计算()
    {
        var withEmpty = new Dictionary<string, string>
        {
            ["app_id"] = "abc",
            ["extra"] = "",
        };
        var withoutExtra = new Dictionary<string, string>
        {
            ["app_id"] = "abc",
        };

        // 多一个空值参数会多出 "extra="，结果必须不同
        Assert.NotEqual(Sign(withoutExtra, Secret1), Sign(withEmpty, Secret1));
    }

    [Fact]
    public void 签名_密钥不同结果不同()
    {
        var p = new Dictionary<string, string> { ["app_id"] = "abc" };

        Assert.NotEqual(Sign(p, "secret-a"), Sign(p, "secret-b"));
    }

    [Theory]
    [InlineData("abcdef")]
    [InlineData("")]
    [InlineData("a b&c")]
    [InlineData("你好")]
    public void 签名_输出恒为64位小写十六进制(string value)
    {
        var p = new Dictionary<string, string> { ["q"] = value };
        var sig = Sign(p, Secret1);

        Assert.Equal(64, sig.Length);
        Assert.Matches("^[0-9a-f]{64}$", sig);
    }
}
