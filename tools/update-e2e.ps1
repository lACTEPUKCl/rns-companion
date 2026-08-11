# update-e2e.ps1 — e2e тест автообновления: запускает старую версию из временной
# папки, ждёт плашку обновления, жмёт «Обновить», проверяет самозамену на новую.
param([string]$OldVersion = "1.3.1")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes

$dir = Join-Path $env:TEMP "rns-update-test"
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $dir "RNS.Companion.exe"

Write-Host "== download v$OldVersion"
Invoke-WebRequest -Uri "https://github.com/lACTEPUKCl/rns-companion/releases/download/v$OldVersion/RNS.Companion.exe" -OutFile $exe

Get-Process RNS.Companion -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
$proc = Start-Process -FilePath $exe -PassThru
Write-Host "== started old pid=$($proc.Id), file version: $((Get-Item $exe).VersionInfo.FileVersion)"

# ждём плашку обновления (проверка при старте)
$ae = [System.Windows.Automation.AutomationElement]
$btnUpdate = $null
$target = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("0J7QsdC90L7QstC40YLRjA==")) # «Обновить»
for ($i = 0; $i -lt 20 -and -not $btnUpdate; $i++) {
  Start-Sleep -Seconds 2
  $win = $ae::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "RNS Companion")))
  if (-not $win) { continue }
  $btns = $win.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
  foreach ($b in $btns) { if ($b.Current.Name -eq $target) { $btnUpdate = $b; break } }
}
if (-not $btnUpdate) { Write-Host "FAIL: update button not shown"; Stop-Process -Id $proc.Id -Force; exit 1 }
Write-Host "== update bar shown, clicking"
$btnUpdate.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

# ждём самозамену: exe должен стать новой версии, процесс — перезапуститься (новый pid)
$ok = $false
$expectedMin = [version]"$OldVersion.0"
for ($i = 0; $i -lt 90; $i++) {
  Start-Sleep -Seconds 3
  $v = [version](Get-Item $exe).VersionInfo.FileVersion
  $running = Get-Process RNS.Companion -ErrorAction SilentlyContinue
  if ($v -gt $expectedMin -and $running -and $running.Id -ne $proc.Id) {
    $ok = $true; Write-Host "== swapped to $v, new pid=$($running.Id) (old was $($proc.Id))"; break
  }
  if ($i % 5 -eq 0) { Write-Host "   waiting... file=$v" }
}
if (-not $ok) { Write-Host "FAIL: no swap in 270s"; exit 1 }
Write-Host "UPDATE-E2E-OK"
