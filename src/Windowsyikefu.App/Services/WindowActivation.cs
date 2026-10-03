using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Windowsyikefu.Services;

/// <summary>把窗口强制拉到最前（从浏览器 / 托盘唤起时，仅 Activate() 常被系统拦截）</summary>
public static class WindowActivation
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    public static void BringToFront(Window? window)
    {
        if (window == null) return;

        try
        {
            if (!window.IsVisible) window.Show();

            var hwnd = new WindowInteropHelper(window).Handle;

            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }

            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();

            // 经典抢前台技巧。已是常驻置顶（用户开了顶置按钮）就跳过，避免被顶下去
            if (!window.Topmost)
            {
                window.Topmost = true;
                window.Topmost = false;
            }

            window.Focus();
        }
        catch { /* 置顶失败不影响主流程 */ }
    }
}
