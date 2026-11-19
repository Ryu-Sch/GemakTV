# Generates GemakTV's vector icons and bitmap launcher art. Re-run after changing the design.
# Windows PowerShell 5.1 (System.Drawing). Icons are Material Design icons (Apache 2.0).
param([string]$Root = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$icons = @{
    'ic_channel_list' = 'M3,13h2v-2H3v2zM3,17h2v-2H3v2zM3,9h2V7H3v2zM7,13h14v-2H7v2zM7,17h14v-2H7v2zM7,7v2h14V7H7z'
    'ic_update'       = 'M19,9h-4V3H9v6H5l7,7 7,-7zM5,18v2h14v-2H5z'
    'ic_error'        = 'M11,15h2v2h-2zM11,7h2v6h-2zM11.99,2C6.47,2 2,6.48 2,12s4.47,10 9.99,10C17.52,22 22,17.52 22,12S17.52,2 11.99,2zM12,20c-4.42,0 -8,-3.58 -8,-8s3.58,-8 8,-8 8,3.58 8,8 -3.58,8 -8,8z'
    'ic_ok'           = 'M12,2C6.48,2 2,6.48 2,12s4.48,10 10,10 10,-4.48 10,-10S17.52,2 12,2zM10,17l-5,-5 1.41,-1.41L10,14.17l7.59,-7.59L19,8l-9,9z'
    'ic_tv'           = 'M21,3H3c-1.1,0 -2,0.9 -2,2v12c0,1.1 0.9,2 2,2h5v2h8v-2h5c1.1,0 1.99,-0.9 1.99,-2L23,5c0,-1.1 -0.9,-2 -2,-2zM21,17H3V5h18v12z'
    'ic_wifi_off'     = 'M23.64,7c-0.45,-0.34 -4.93,-4 -11.64,-4 -1.5,0 -2.89,0.19 -4.15,0.48L18.18,13.8 23.64,7zM17.04,15.22L3.27,1.44 2,2.72l2.05,2.06C1.91,5.76 0.59,6.82 0.36,7l11.63,14.49 0.01,0.01 0.01,-0.01 3.9,-4.86 3.32,3.32 1.27,-1.27 -3.46,-3.46z'
    'ic_settings'     = 'M19.14,12.94c0.04,-0.3 0.06,-0.61 0.06,-0.94 0,-0.32 -0.02,-0.64 -0.07,-0.94l2.03,-1.58c0.18,-0.14 0.23,-0.41 0.12,-0.61l-1.92,-3.32c-0.12,-0.22 -0.37,-0.29 -0.59,-0.22l-2.39,0.96c-0.5,-0.38 -1.03,-0.7 -1.62,-0.94L14.4,2.81c-0.04,-0.24 -0.24,-0.41 -0.48,-0.41h-3.84c-0.24,0 -0.43,0.17 -0.47,0.41L9.25,5.35C8.66,5.59 8.12,5.92 7.63,6.29L5.24,5.33c-0.22,-0.08 -0.47,0 -0.59,0.22L2.74,8.87C2.62,9.08 2.66,9.34 2.86,9.48l2.03,1.58C4.84,11.36 4.8,11.69 4.8,12s0.02,0.64 0.07,0.94l-2.03,1.58c-0.18,0.14 -0.23,0.41 -0.12,0.61l1.92,3.32c0.12,0.22 0.37,0.29 0.59,0.22l2.39,-0.96c0.5,0.38 1.03,0.7 1.62,0.94l0.36,2.54c0.05,0.24 0.24,0.41 0.48,0.41h3.84c0.24,0 0.44,-0.17 0.47,-0.41l0.36,-2.54c0.59,-0.24 1.13,-0.56 1.62,-0.94l2.39,0.96c0.22,0.08 0.47,0 0.59,-0.22l1.92,-3.32c0.12,-0.22 0.07,-0.47 -0.12,-0.61L19.14,12.94zM12,15.6c-1.98,0 -3.6,-1.62 -3.6,-3.6s1.62,-3.6 3.6,-3.6 3.6,1.62 3.6,3.6S13.98,15.6 12,15.6z'
    'ic_person'       = 'M12,12c2.21,0 4,-1.79 4,-4s-1.79,-4 -4,-4 -4,1.79 -4,4 1.79,4 4,4zM12,14c-2.67,0 -8,1.34 -8,4v2h16v-2c0,-2.66 -5.33,-4 -8,-4z'
    'ic_wifi'         = 'M1,9l2,2c4.97,-4.97 13.03,-4.97 18,0l2,-2C16.93,2.93 7.08,2.93 1,9zM9,17l3,3 3,-3c-1.65,-1.66 -4.34,-1.66 -6,0zM5,13l2,2c2.76,-2.76 7.24,-2.76 10,0l2,-2C15.14,9.14 8.87,9.14 5,13z'
    'ic_close'        ='M19,6.41L17.59,5 12,10.59 6.41,5 5,6.41 10.59,12 5,17.59 6.41,19 12,13.41 17.59,19 19,17.59 13.41,12z'
    'ic_add'          = 'M19,13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z'
    'ic_delete'       = 'M6,19c0,1.1 0.9,2 2,2h8c1.1,0 2,-0.9 2,-2V7H6v12zM19,4h-3.5l-1,-1h-5l-1,1H5v2h14V4z'
    'ic_search'       = 'M15.5,14h-0.79l-0.28,-0.27C15.41,12.59 16,11.11 16,9.5 16,5.91 13.09,3 9.5,3S3,5.91 3,9.5 5.91,16 9.5,16c1.61,0 3.09,-0.59 4.23,-1.57l0.27,0.28v0.79l5,4.99L20.49,19l-4.99,-5zM9.5,14C7.01,14 5,11.99 5,9.5S7.01,5 9.5,5 14,7.01 14,9.5 11.99,14 9.5,14z'
    'ic_arrow_up'     = 'M7.41,15.41L12,10.83l4.59,4.58L18,14l-6,-6 -6,6z'
    'ic_arrow_down'   = 'M7.41,8.59L12,13.17l4.59,-4.58L18,10l-6,6 -6,-6z'
    'ic_cloud_upload' = 'M19.35,10.04C18.67,6.59 15.64,4 12,4 9.11,4 6.6,5.64 5.35,8.04 2.34,8.36 0,10.91 0,14c0,3.31 2.69,6 6,6h13c2.76,0 5,-2.24 5,-5 0,-2.64 -2.05,-4.78 -4.65,-4.96zM14,13v4h-4v-4H7l5,-5 5,5h-3z'
    'ic_logout'       = 'M10.09,15.59L11.5,17l5,-5 -5,-5 -1.41,1.41L12.67,11H3v2h9.67l-2.58,2.59zM19,3H5c-1.11,0 -2,0.9 -2,2v4h2V5h14v14H5v-4H3v4c0,1.1 0.89,2 2,2h14c1.1,0 2,-0.9 2,-2V5c0,-1.1 -0.9,-2 -2,-2z'
    'ic_refresh'      = 'M17.65,6.35C16.2,4.9 14.21,4 12,4c-4.42,0 -7.99,3.58 -7.99,8s3.57,8 7.99,8c3.73,0 6.84,-2.55 7.73,-6h-2.08c-0.82,2.33 -3.04,4 -5.65,4 -3.31,0 -6,-2.69 -6,-6s2.69,-6 6,-6c1.66,0 3.14,0.69 4.22,1.78L13,11h7V4l-2.35,2.35z'
}

function Write-Vector($dir, $name, $path, $size) {
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<vector xmlns:android="http://schemas.android.com/apk/res/android" android:width="${size}dp" android:height="${size}dp" android:viewportWidth="24" android:viewportHeight="24">
  <path android:fillColor="#FFFFFFFF" android:pathData="$path" />
</vector>
"@
    [IO.File]::WriteAllText((Join-Path $dir "$name.xml"), $xml.Replace("`r`n", "`n"))
}

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    $p
}

# The GemakTV mark: an amber TV with a play triangle, on a dark rounded tile.
function Draw-Mark($g, [float]$x, [float]$y, [float]$s, $tileTop, $tileBottom, [bool]$manager) {
    $g.SmoothingMode = 'AntiAlias'
    $tile = New-RoundedRect $x $y $s $s ($s * 0.22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF($x, $y)), (New-Object System.Drawing.PointF($x, ($y + $s))), $tileTop, $tileBottom)
    $g.FillPath($brush, $tile)
    $amber = [System.Drawing.Color]::FromArgb(255, 255, 197, 49)
    $pen = New-Object System.Drawing.Pen($amber, ($s * 0.065))
    $pen.LineJoin = 'Round'
    $screen = New-RoundedRect ($x + $s * 0.18) ($y + $s * 0.24) ($s * 0.64) ($s * 0.44) ($s * 0.07)
    $g.DrawPath($pen, $screen)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, ($x + $s * 0.40), ($y + $s * 0.80), ($x + $s * 0.60), ($y + $s * 0.80))
    $g.DrawLine($pen, ($x + $s * 0.50), ($y + $s * 0.70), ($x + $s * 0.50), ($y + $s * 0.80))
    $fill = New-Object System.Drawing.SolidBrush($amber)
    if ($manager) {
        # Three list lines for the manager
        foreach ($i in 0..2) {
            $ly = $y + $s * (0.36 + 0.10 * $i)
            $g.FillEllipse($fill, ($x + $s * 0.29), ($ly - $s * 0.025), ($s * 0.05), ($s * 0.05))
            $g.FillRectangle($fill, ($x + $s * 0.38), ($ly - $s * 0.02), ($s * 0.32), ($s * 0.04))
        }
    } else {
        $tri = @(
            (New-Object System.Drawing.PointF(($x + $s * 0.44), ($y + $s * 0.34))),
            (New-Object System.Drawing.PointF(($x + $s * 0.44), ($y + $s * 0.58))),
            (New-Object System.Drawing.PointF(($x + $s * 0.62), ($y + $s * 0.46))))
        $g.FillPolygon($fill, $tri)
    }
}

function Save-Icon($path, [int]$size, [bool]$manager) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($manager) { $top = [System.Drawing.Color]::FromArgb(255, 21, 94, 117); $bottom = [System.Drawing.Color]::FromArgb(255, 12, 52, 66) }
    else { $top = [System.Drawing.Color]::FromArgb(255, 44, 50, 64); $bottom = [System.Drawing.Color]::FromArgb(255, 16, 18, 24) }
    Draw-Mark $g ($size * 0.04) ($size * 0.04) ($size * 0.92) $top $bottom $manager
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Save-Banner($path) {
    # Android TV launcher banner: 320x180 at xhdpi.
    $bmp = New-Object System.Drawing.Bitmap(320, 180)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(320, 180)), [System.Drawing.Color]::FromArgb(255, 38, 43, 54), [System.Drawing.Color]::FromArgb(255, 14, 16, 22))
    $g.FillRectangle($bg, 0, 0, 320, 180)
    Draw-Mark $g 22 40 100 ([System.Drawing.Color]::FromArgb(255, 52, 58, 74)) ([System.Drawing.Color]::FromArgb(255, 24, 27, 35)) $false
    $font = New-Object System.Drawing.Font('Segoe UI', 30, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $small = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('Gemak', $font, [System.Drawing.Brushes]::White, 134, 58)
    $g.DrawString('TV', $font, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 197, 49))), 236, 58)
    $g.DrawString('easy television', $small, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 184, 192, 204))), 137, 98)
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

foreach ($app in 'GemakTV.Tv', 'GemakTV.Manager') {
    $res = Join-Path $Root "src\$app\Resources"
    $drawable = Join-Path $res 'drawable'
    New-Item -ItemType Directory -Force $drawable | Out-Null
    foreach ($name in $icons.Keys) {
        # One intrinsic size: buttons show icons at this size; the status and dialog ImageViews scale them up.
        Write-Vector $drawable $name $icons[$name] 40
    }
    $isManager = $app -eq 'GemakTV.Manager'
    Save-Icon (Join-Path $res 'mipmap-xxxhdpi\ic_launcher.png') 192 $isManager
    Save-Icon (Join-Path $res 'mipmap-xxhdpi\ic_launcher.png') 144 $isManager
    Save-Icon (Join-Path $res 'mipmap-xhdpi\ic_launcher.png') 96 $isManager
    Save-Icon (Join-Path $res 'mipmap-hdpi\ic_launcher.png') 72 $isManager
    Save-Icon (Join-Path $res 'mipmap-mdpi\ic_launcher.png') 48 $isManager
}
Save-Banner (Join-Path $Root 'src\GemakTV.Tv\Resources\drawable-xhdpi\banner.png')
Save-Icon (Join-Path $Root 'docs\images\icon-tv.png') 256 $false
Save-Icon (Join-Path $Root 'docs\images\icon-manager.png') 256 $true
Save-Banner (Join-Path $Root 'docs\images\banner.png')
Write-Host 'Art generated.'
