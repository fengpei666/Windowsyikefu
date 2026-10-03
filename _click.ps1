param([int]$X = 125, [int]$Y = 18)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Clicker {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
}
"@

$proc = Get-Process Windowsyikefu | Select-Object -First 1
$h = $proc.MainWindowHandle
[Clicker]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 600

$lp = [IntPtr](($Y -shl 16) -bor $X)
[Clicker]::SendMessage($h, 0x0200, [IntPtr]::Zero, $lp) | Out-Null   # WM_MOUSEMOVE
Start-Sleep -Milliseconds 120
[Clicker]::SendMessage($h, 0x0201, [IntPtr]1, $lp) | Out-Null        # WM_LBUTTONDOWN
Start-Sleep -Milliseconds 90
[Clicker]::SendMessage($h, 0x0202, [IntPtr]::Zero, $lp) | Out-Null   # WM_LBUTTONUP
Start-Sleep -Milliseconds 1600

$wr = New-Object Clicker+RECT
[Clicker]::GetWindowRect($h, [ref]$wr) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($wr.Left, $wr.Top, 0, 0, $bmp.Size)
$out = Join-Path $PSScriptRoot "_shot.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"clicked $X,$Y and saved"
