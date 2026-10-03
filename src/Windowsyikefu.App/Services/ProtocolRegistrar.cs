using Microsoft.Win32;

namespace Windowsyikefu.Services;

/// <summary>
/// 注册自定义 URL 协议（默认 easykefu://）。
/// 写当前用户 HKCU\Software\Classes，无需管理员权限，卸载时删同一个键即可。
/// 采用"每次启动校正一次"的幂等策略，程序挪目录后依然指向当前 exe。
/// </summary>
public static class ProtocolRegistrar
{
    private static string KeyPath(string scheme) => @"Software\Classes\" + scheme;

    /// <summary>注册/校正协议，返回是否写入成功</summary>
    public static bool EnsureRegistered(string scheme)
    {
        if (!DeepLinkParser.IsValidScheme(scheme)) return false;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var command = $"\"{exe}\" \"%1\"";

        try
        {
            // 已经指向当前 exe 就不重复写，避免每次启动都动注册表
            if (GetRegisteredCommand(scheme) == command) return true;

            using var root = Registry.CurrentUser.CreateSubKey(KeyPath(scheme));
            if (root == null) return false;

            root.SetValue("", "URL:易客服绑定协议");
            root.SetValue("URL Protocol", "");
            root.SetValue("FriendlyTypeName", "易客服绑定协议");

            using var cmd = root.CreateSubKey(@"shell\open\command");
            if (cmd == null) return false;
            cmd.SetValue("", command);

            return true;
        }
        catch { return false; }
    }

    /// <summary>读取当前已注册的启动命令（未注册返回 null）</summary>
    public static string? GetRegisteredCommand(string scheme)
    {
        if (!DeepLinkParser.IsValidScheme(scheme)) return null;

        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey(KeyPath(scheme) + @"\shell\open\command", false);
            return cmd?.GetValue("") as string;
        }
        catch { return null; }
    }

    public static bool IsRegistered(string scheme) => GetRegisteredCommand(scheme) != null;

    /// <summary>注销协议</summary>
    public static void Unregister(string scheme)
    {
        if (!DeepLinkParser.IsValidScheme(scheme)) return;
        try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath(scheme), false); }
        catch { }
    }
}
