# smoke-shot.ps1 — запускает приложение, снимает окно через PrintWindow, завершает процесс.
param(
  [string]$Exe = "publish/RNS.Companion.exe",
  [string]$Out = "smoke-shot.png",
  [int]$WaitSeconds = 7
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Shot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
Start-Sleep -Seconds $WaitSeconds

$proc.Refresh()
if ($proc.HasExited) {
  Write-Host "FAIL: process exited early (code $($proc.ExitCode))"
  exit 1
}

$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
  Write-Host "FAIL: no main window"
  Stop-Process -Id $proc.Id -Force
  exit 1
}

$r = New-Object Win32Shot+RECT
[Win32Shot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Host "Window rect: ${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Win32Shot]::PrintWindow($hwnd, $hdc, 2)  # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
Write-Host "PrintWindow: $ok"
$bmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Screenshot: $Out"

Stop-Process -Id $proc.Id -Force
Write-Host "OK: app was alive, smoke test done"
