# png2ico.ps1 — собирает многослойный .ico из PNG (PNG-compressed entries, Vista+)
param(
  [string]$Src = "src/RnsCompanion/Assets/logo.png",
  [string]$Dst = "src/RnsCompanion/Assets/app.ico"
)
Add-Type -AssemblyName System.Drawing

$srcImg = [System.Drawing.Image]::FromFile((Resolve-Path $Src))
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($srcImg, 0, 0, $s, $s)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,$ms.ToArray()
  $bmp.Dispose()
}
$srcImg.Dispose()

$fs = [System.IO.File]::Create((Join-Path (Get-Location) $Dst))
$bw = New-Object System.IO.BinaryWriter $fs
$count = $pngs.Count
$bw.Write([uint16]0)      # reserved
$bw.Write([uint16]1)      # type = icon
$bw.Write([uint16]$count) # count
$offset = 6 + 16 * $count
for ($i = 0; $i -lt $count; $i++) {
  $s = $sizes[$i]
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))) # width (0 = 256)
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))) # height
  $bw.Write([byte]0)          # colors in palette
  $bw.Write([byte]0)          # reserved
  $bw.Write([uint16]1)        # color planes
  $bw.Write([uint16]32)       # bits per pixel
  $bw.Write([uint32]$pngs[$i].Length)
  $bw.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush(); $fs.Close()
Write-Host "ICO written: $Dst ($((Get-Item $Dst).Length) bytes, sizes: $($sizes -join ', '))"
