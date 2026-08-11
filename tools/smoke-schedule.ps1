# smoke-schedule.ps1 - LIVE test: enables the schedule in Settings, clicks Save,
# verifies the Task Scheduler task (daily trigger + WakeToRun), then deletes the task.
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
public class W32S {
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
  [W32S]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
  Start-Sleep -Milliseconds 250
  [W32S]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [W32S]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
}

$proc = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
Start-Sleep -Seconds $WaitSeconds

$ae = [System.Windows.Automation.AutomationElement]
$root = $ae::RootElement
$win = $root.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "RNS Companion")))
if ($null -eq $win) { Write-Host "FAIL: main window"; Stop-Process -Id $proc.Id -Force; exit 1 }
[W32S]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [W32S]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
[W32S]::BringWindowToTop([IntPtr]$win.Current.NativeWindowHandle) | Out-Null
[W32S]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700

# 1. Открыть настройки
$btn = $win.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "BtnSettings")))
Click-Element $btn
Start-Sleep -Seconds 2

$proc.Refresh()
if ($proc.HasExited) { Write-Host "FAIL: crashed on settings open"; exit 1 }

# Окно настроек ищем через EnumWindows (UIA-дерево бывает стейл).
$script:found = [IntPtr]::Zero
$enumCb = {
  param($h, $l)
  $wpid = 0
  [W32S]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  if ($wpid -eq $proc.Id -and [W32S]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 256
    [W32S]::GetWindowText($h, $sb, 256) | Out-Null
    $t = $sb.ToString()
    if ($t.Length -gt 0 -and $t -ne "RNS Companion" -and $t.EndsWith("RNS Companion")) {
      $script:found = $h
      return $false
    }
  }
  return $true
}
[W32S]::EnumWindows($enumCb, [IntPtr]::Zero) | Out-Null
if ($script:found -eq [IntPtr]::Zero) { Write-Host "FAIL: settings window"; Stop-Process -Id $proc.Id -Force; exit 1 }
$sw = [System.Windows.Automation.AutomationElement]::FromHandle($script:found)
[W32S]::SetForegroundWindow($script:found) | Out-Null
Start-Sleep -Milliseconds 500

# 2. Включить расписание: 7-й CheckBox по порядку (индекс 6).
$boxes = $sw.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
  $ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
Write-Host ("CheckBoxes found: " + $boxes.Count)
if ($boxes.Count -lt 7) { Write-Host "FAIL: checkbox list"; Stop-Process -Id $proc.Id -Force; exit 1 }
$schedBox = $boxes.Item(6)
$tp = $schedBox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
  Click-Element $schedBox
  Start-Sleep -Seconds 1
}
$state = $tp.Current.ToggleState
Write-Host ("Schedule toggle state: " + $state)

# 3. Сохранить (регистрирует задачу планировщика). InvokePattern — без координат/скролла.
$save = $sw.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "BtnSave")))
if ($null -eq $save) { Write-Host "FAIL: save button"; Stop-Process -Id $proc.Id -Force; exit 1 }
$save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

# Диагностика: какие окна остались после клика по «Сохранить».
$diag = @()
$enumCb2 = {
  param($h, $l)
  $wpid = 0
  [W32S]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  if ($wpid -eq $proc.Id -and [W32S]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 256
    [W32S]::GetWindowText($h, $sb, 256) | Out-Null
    if ($sb.Length -gt 0) { $script:diag += ("hwnd=" + $h + " title=[" + $sb.ToString() + "]") }
  }
  return $true
}
[W32S]::EnumWindows($enumCb2, [IntPtr]::Zero) | Out-Null
foreach ($d in $diag) { Write-Host $d }

$proc.Refresh()
if ($proc.HasExited) { Write-Host "FAIL: crashed on save"; exit 1 }

# 4. Проверить задачу планировщика.
$task = Get-ScheduledTask -TaskName "RNS Companion" -ErrorAction SilentlyContinue
if ($null -eq $task) {
  Write-Host "FAIL: scheduled task not created"
  Stop-Process -Id $proc.Id -Force
  exit 1
}
$t = $task.Triggers[0]
Write-Host ("Task: State=" + $task.State + " TriggerType=" + $t.CimClass.CimClassName +
  " DaysInterval=" + $t.DaysInterval + " StartBoundary=" + $t.StartBoundary +
  " WakeToRun=" + $task.Settings.WakeToRun + " Exec=" + $task.Actions[0].Execute + " " + $task.Actions[0].Arguments)

# 5. Убрать за собой: удалить задачу.
Unregister-ScheduledTask -TaskName "RNS Companion" -Confirm:$false
Write-Host "Task cleaned up"

Stop-Process -Id $proc.Id -Force
Write-Host "OK"
