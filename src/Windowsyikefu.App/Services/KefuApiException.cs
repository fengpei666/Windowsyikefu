namespace Windowsyikefu.Services;

/// <summary>接口调用异常（含服务器返回的业务错误码）</summary>
public sealed class KefuApiException : Exception
{
    public int Code { get; }

    public KefuApiException(int code, string message) : base(message) => Code = code;

    public static string Describe(int code) => code switch
    {
        0 => "成功",
        -1 => "业务错误",
        1001 => "缺少认证参数或时间戳格式错误",
        1002 => "请求已过期（本机时间与服务端偏差超过 300 秒）",
        1003 => "APP 不存在或已被禁用",
        1004 => "签名验证失败，请检查 App Secret",
        _ => "未知错误"
    };
}
