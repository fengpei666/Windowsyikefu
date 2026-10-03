using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Windowsyikefu.Services;

/// <summary>
/// Windows 10 / 11 原生通知（Action Center 通知中心），支持在通知里直接快捷回复。
/// 应用为「未打包 Win32 程序」，由 Toolkit 通过 ToastNotificationManagerCompat 自动注册 AUMID。
/// </summary>
public static class ToastService
{
    public const string ArgAction = "action";
    public const string ArgSessionId = "sessionId";
    public const string ArgMessageId = "messageId";

    /// <summary>消息来自哪个站点（站点唯一标识），点通知时先切到该站点</summary>
    public const string ArgSiteKey = "site";

    public const string ActionOpen = "open";
    public const string ActionReply = "reply";

    public const string ReplyBoxId = "kefuReplyBox";

    /// <summary>发出「新消息」通知，内含输入框可直接快捷回复。</summary>
    /// <returns>是否成功发出系统通知</returns>
    public static bool ShowNewMessage(
        long sessionId,
        long messageId,
        string title,
        string content,
        string? avatarPath = null,
        bool allowReply = true,
        string? siteKey = null)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ArgAction, ActionOpen)
                .AddArgument(ArgSessionId, sessionId.ToString())
                .AddArgument(ArgSiteKey, siteKey ?? "")
                .AddHeader("kefu-msg", "易客服", "新消息")
                .AddText(title)
                .AddText(content);

            if (!string.IsNullOrWhiteSpace(avatarPath) && File.Exists(avatarPath))
            {
                builder.AddAppLogoOverride(new Uri(avatarPath!), ToastGenericAppLogoCrop.Circle);
            }

            if (allowReply)
            {
                builder
                    .AddInputTextBox(ReplyBoxId, placeHolderContent: "快捷回复，回车发送…")
                    .AddButton(new ToastButton()
                        .SetContent("发送")
                        .AddArgument(ArgAction, ActionReply)
                        .AddArgument(ArgSessionId, sessionId.ToString())
                        .AddArgument(ArgSiteKey, siteKey ?? "")
                        .AddArgument(ArgMessageId, messageId.ToString())
                        .SetTextBoxId(ReplyBoxId)
                        .SetBackgroundActivation())
                    .AddButton(new ToastButton()
                        .SetContent("打开会话")
                        .AddArgument(ArgAction, ActionOpen)
                        .AddArgument(ArgSessionId, sessionId.ToString())
                        .AddArgument(ArgSiteKey, siteKey ?? "")
                        .SetBackgroundActivation());
            }
            else
            {
                builder.AddButton(new ToastButton()
                    .SetContent("打开会话")
                    .AddArgument(ArgAction, ActionOpen)
                    .AddArgument(ArgSessionId, sessionId.ToString())
                    .AddArgument(ArgSiteKey, siteKey ?? "")
                    .SetBackgroundActivation());
            }

            builder.Show();
            return true;
        }
        catch
        {
            // 通知失败不能影响主流程（例如系统关闭了通知）
            return false;
        }
    }

    /// <summary>普通提示通知</summary>
    public static void ShowInfo(string title, string content)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(content)
                .Show();
        }
        catch { }
    }

    /// <summary>清理本应用注册的通知平台资源（退出时调用）</summary>
    public static void Uninstall()
    {
        try { ToastNotificationManagerCompat.Uninstall(); } catch { }
    }
}
