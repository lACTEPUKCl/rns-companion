# smoke-settings.ps1 - opens Settings via a real mouse click (with foreground-unlock trick),
# captures it, kills the app. ASCII-only.
param(
  [string]$Exe = "publish/RNS.Companion.exe",
  [string]$Out = "smoke-settings.png",
  [int]$WaitSeconds = 6
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Shot2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
Start-Sleep -Seconds $WaitSeconds

$ae = [System.Windows.Automation.AutomationElement]
$root = $ae::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
  (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "RNS Companion")))
if ($null -eq $win) { Write-Host "FAIL: main window not found"; Stop-Process -Id $proc.Id -Force; exit 1 }

$mainHwnd = [IntPtr]$win.Current.NativeWindowHandle

# Foreground lock workaround: прокидываем Alt, затем выводим окно наверх.
[Win32Shot2]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)      # Alt down
[Win32Shot2]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)      # Alt up
[Win32Shot2]::ShowWindow($mainHwnd, 9) | Out-Null          # SW_RESTORE
[Win32Shot2]::BringWindowToTop($mainHwnd) | Out-Null
[Win32Shot2]::SetForegroundWindow($mainHwnd) | Out-Null
Start-Sleep -Milliseconds 700

$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "BtnSettings")))
if ($null -eq $btn) { Write-Host "FAIL: settings button not found"; Stop-Process -Id $proc.Id -Force; exit 1 }

$rect = $btn.Current.BoundingRectangle
[Win32Shot2]::SetCursorPos([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2)) | Out-Null
Start-Sleep -Milliseconds 300
[Win32Shot2]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[Win32Shot2]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Seconds 2

$proc.Refresh()
if ($proc.HasExited) { Write-Host "FAIL: app crashed after opening settings"; exit 1 }

# Окно настроек: видимое top-level окно того же процесса с заголовком, отличным от главного.
$mainHwnd = [IntPtr]$win.Current.NativeWindowHandle
$swHwnd = [IntPtr]::Zero
$script:found = [IntPtr]::Zero
$enumCb = {
  param($h, $l)
  $wpid = 0
  [Win32Shot2]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  if ($wpid -eq $proc.Id -and [Win32Shot2]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 256
    [Win32Shot2]::GetWindowText($h, $sb, 256) | Out-Null
    $t = $sb.ToString()
    if ($t.Length -gt 0 -and $t -ne "RNS Companion" -and $t.EndsWith("RNS Companion")) {
      $script:found = $h
      return $false
    }
  }
  return $true
}
[Win32Shot2]::EnumWindows($enumCb, [IntPtr]::Zero) | Out-Null
$swHwnd = $script:found
if ($swHwnd -eq [IntPtr]::Zero) { Write-Host "FAIL: settings window not found"; Stop-Process -Id $proc.Id -Force; exit 1 }

$r = New-Object Win32Shot2+RECT
[Win32Shot2]::GetWindowRect($swHwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Host "Settings window rect: ${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Win32Shot2]::PrintWindow($swHwnd, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
Write-Host "PrintWindow: $ok"
$bmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Screenshot: $Out"

Stop-Process -Id $proc.Id -Force
Write-Host "OK"
