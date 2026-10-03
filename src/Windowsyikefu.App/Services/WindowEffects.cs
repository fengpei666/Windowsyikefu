using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Windowsyikefu.Services;

/// <summary>
/// 窗口视觉效果：
/// Windows 11 (22000+) → Mica 云母材质（DWMWA_SYSTEMBACKDROP_TYPE）
/// Windows 10 (1809+)  → Acrylic 亚克力模糊（SetWindowCompositionAttribute）
/// 更早系统            → 不启用模糊，由调用方使用不透明背景
/// </summary>
internal static class WindowEffects
{
    // ---- DWM ----
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;   // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    // ---- 未公开的 SetWindowCompositionAttribute（Win10 亚克力）----
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor; // AABBGGRR
        public int AnimationId;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>当前系统是否 Windows 11 (build >= 22000)</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>是否支持 Mica（Windows 11 22H2 / build >= 22621）</summary>
    public static bool SupportsMica => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>没开成材质时的兜底色，与主窗口一致</summary>
    private static readonly Brush FallbackBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF5));

    /// <summary>
    /// 给任意窗口套上系统材质，所有窗口统一走这个入口，保证观感一致：
    /// 成功 → 窗口背景透明，让 Acrylic / Mica 透出来（XAML 里的半透明根 Border 负责提亮）；
    /// 失败（老系统、关了「透明效果」）→ 退回不透明浅灰，界面照常可用。
    /// </summary>
    public static void ApplyBackdrop(Window window)
        => window.Background = TryEnableBackdrop(window) ? Brushes.Transparent : FallbackBrush;

    /// <summary>
    /// 尝试为窗口启用系统材质。返回 true 表示已启用模糊类材质，
    /// 调用方此时应把窗口背景设为半透明。
    /// </summary>
    public static bool TryEnableBackdrop(Window window, bool dark = false)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // 圆角
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // 跟随应用主题（浅色）
            int darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            if (SupportsMica)
            {
                // 让 DWM 绘制整个客户区背景
                var src = HwndSource.FromHwnd(hwnd);
                if (src?.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = Colors.Transparent;

                var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                // 优先 Acrylic（TRANSIENTWINDOW）：模糊的是窗口后面的实际内容，肉眼能看出「毛玻璃」；
                // Mica（MAINWINDOW）只采样壁纸，浅色主题下几乎和纯色一样，等于没效果。
                int backdrop = DWMSBT_TRANSIENTWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                    return true;

                // Acrylic 不被支持时退回 Mica
                backdrop = DWMSBT_MAINWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                    return true;
            }

            if (Environment.OSVersion.Version.Build >= 17763)
                return TryEnableAcrylic(hwnd, dark);
        }
        catch
        {
            // 任何失败都退回不透明背景
        }

        return false;
    }

    private static bool TryEnableAcrylic(IntPtr hwnd, bool dark)
    {
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null)
            src.CompositionTarget.BackgroundColor = Colors.Transparent;

        // 浅色：接近白色；深色：接近黑色。颜色为 AABBGGRR
        int gradient = dark ? unchecked((int)0x992A2A2A) : unchecked((int)0xB3F6F6F9);

        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = gradient,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
