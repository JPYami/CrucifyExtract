param(
  [string]$Out = (Join-Path $PSScriptRoot "..\Assets\crucify.ico"),
  [string]$Preview = (Join-Path $PSScriptRoot "..\Assets\icon-preview.png")
)

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [Math]::Max(0.1, $r * 2)
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

$OutDir = Split-Path -Parent $Out
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = New-Object System.Collections.ArrayList

foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)

  $pad = [float]($s * 0.03)
  $tile = New-RoundedPath $pad $pad ([float]($s - 2 * $pad)) ([float]($s - 2 * $pad)) ([float]($s * 0.24))
  $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(0, 0)),
    (New-Object System.Drawing.PointF($s, $s)),
    [System.Drawing.Color]::FromArgb(255, 30, 16, 36),
    [System.Drawing.Color]::FromArgb(255, 9, 6, 13))
  $g.FillPath($bgBrush, $tile)
  $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(95, 255, 120, 160), [float]([Math]::Max(0.5, $s * 0.013)))
  $g.DrawPath($rimPen, $tile)

  $bar = [float]($s * 0.15)
  $cx = [float]($s / 2.0)
  $cy = [float]($s / 2.0)
  $arm = [float]($s * 0.285)
  $vTop = [float]($cy - $s * 0.295)
  $vBot = [float]($cy + $s * 0.295)

  $glowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(55, 255, 46, 99))
  $glowV = New-RoundedPath ([float]($cx - $bar)) ([float]($vTop - $bar * 0.35)) ([float]($bar * 2)) ([float]($vBot - $vTop + $bar * 0.7)) ([float]$bar)
  $glowH = New-RoundedPath ([float]($cx - $arm - $bar * 0.35)) ([float]($cy - $bar)) ([float]($arm * 2 + $bar * 0.7)) ([float]($bar * 2)) ([float]$bar)
  $g.FillPath($glowBrush, $glowV)
  $g.FillPath($glowBrush, $glowH)

  $crossBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(0, 0)),
    (New-Object System.Drawing.PointF($s, $s)),
    [System.Drawing.Color]::FromArgb(255, 255, 74, 122),
    [System.Drawing.Color]::FromArgb(255, 178, 44, 255))
  $vRect = New-RoundedPath ([float]($cx - $bar / 2)) $vTop $bar ([float]($vBot - $vTop)) ([float]($bar / 2))
  $hRect = New-RoundedPath ([float]($cx - $arm)) ([float]($cy - $bar / 2)) ([float]($arm * 2)) $bar ([float]($bar / 2))
  $g.FillPath($crossBrush, $vRect)
  $g.FillPath($crossBrush, $hRect)

  if ($s -ge 32) {
    $hi = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 255, 235, 245), [float]($bar * 0.28))
    $hi.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hi.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($hi, [float]($cx - $arm * 0.55), [float]($cy - $bar * 0.36), [float]($cx + $arm * 0.55), [float]($cy - $bar * 0.36))
    $g.DrawLine($hi, [float]($cx - $bar * 0.36), [float]($vTop + $bar * 0.5), [float]($cx - $bar * 0.36), [float]($cy - $bar * 0.8))
  }

  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  [void]$frames.Add($ms.ToArray())
  if ($s -eq 256) { $bmp.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }

  $g.Dispose()
  $bmp.Dispose()
}

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $data = $frames[$i]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$dim)
  $bw.Write([byte]$dim)
  $bw.Write([byte]0)
  $bw.Write([byte]0)
  $bw.Write([UInt16]1)
  $bw.Write([UInt16]32)
  $bw.Write([UInt32]$data.Length)
  $bw.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($d in $frames) { $bw.Write($d) }
$bw.Flush()
$bw.Close()

Write-Output ("icon written: " + (Resolve-Path $Out).Path)
