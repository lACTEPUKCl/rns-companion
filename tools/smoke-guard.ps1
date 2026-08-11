# smoke-guard.ps1 - enables LowGraphics in Settings, saves, verifies RestoreGuard task, cleans up.
# ASCII-only.
param(
  [string]$Exe = "publish/RNS.Companion.exe",
  [int]$WaitSeconds = 6
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32G {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte s, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
}
"@

function Click-Element($el) {
  $r = $el.Current.BoundingRectangle
  [W32G]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
  Start-Sleep -Milliseconds 250
  [W32G]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [W32G]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
}

$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
Start-Sleep -Seconds $WaitSeconds

$ae = [System.Windows.Automation.AutomationElement]
$root = $ae::RootElement
$win = $root.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "RNS Companion")))
if ($null -eq $win) { Write-Host "FAIL: main window"; Stop-Process -Id $proc.Id -Force; exit 1 }
[W32G]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [W32G]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
[W32G]::BringWindowToTop([IntPtr]$win.Current.NativeWindowHandle) | Out-Null
[W32G]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700

$btn = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "BtnSettings")))
Click-Element $btn
Start-Sleep -Seconds 2

$script:found = [IntPtr]::Zero
$enumCb = {
  param($h, $l)
  $wpid = 0
  [W32G]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  if ($wpid -eq $proc.Id -and [W32G]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 256
    [W32G]::GetWindowText($h, $sb, 256) | Out-Null
    $t = $sb.ToString()
    if ($t.Length -gt 0 -and $t -ne "RNS Companion" -and $t.EndsWith("RNS Companion")) {
      $script:found = $h
      return $false
    }
  }
  return $true
}
[W32G]::EnumWindows($enumCb, [IntPtr]::Zero) | Out-Null
if ($script:found -eq [IntPtr]::Zero) { Write-Host "FAIL: settings window"; Stop-Process -Id $proc.Id -Force; exit 1 }
$sw = [System.Windows.Automation.AutomationElement]::FromHandle($script:found)
[W32G]::SetForegroundWindow($script:found) | Out-Null
Start-Sleep -Milliseconds 500

# CheckBox index 2 = LowGraphics (0=MonitorOff, 1=MonitorOffScheduled, 2=LowGraphics).
$boxes = $sw.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
  $ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
Write-Host ("CheckBoxes found: " + $boxes.Count)
$low = $boxes.Item(2)
$tp = $low.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
  Click-Element $low
  Start-Sleep -Seconds 1
}

$save = $sw.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "BtnSave")))
$save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 4

$proc.Refresh()
if ($proc.HasExited) { Write-Host "FAIL: crashed on save"; exit 1 }

$task = Get-ScheduledTask -TaskName "RNS Companion RestoreGuard" -ErrorAction SilentlyContinue
if ($null -eq $task) {
  Write-Host "FAIL: RestoreGuard task not created"
  Stop-Process -Id $proc.Id -Force
  exit 1
}
Write-Host ("RestoreGuard: State=" + $task.State + " Trigger=" + $task.Triggers[0].CimClass.CimClassName +
  " Exec=" + $task.Actions[0].Execute + " " + $task.Actions[0].Arguments)

Unregister-ScheduledTask -TaskName "RNS Companion RestoreGuard" -Confirm:$false
Write-Host "RestoreGuard cleaned up"

Stop-Process -Id $proc.Id -Force

# Сбрасываем тестовые настройки (LowGraphics остался бы включён).
Remove-Item "$env:LOCALAPPDATA\RNS\Companion\settings.json" -ErrorAction SilentlyContinue
Write-Host "OK"
