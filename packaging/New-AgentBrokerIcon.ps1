#requires -Version 5
<#
.SYNOPSIS
    Recreate AgentBroker's transparent, code-drawn 32px three-node tray icon.
.EXAMPLE
    pwsh -NoProfile -File packaging/New-AgentBrokerIcon.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$directory = Join-Path $repoRoot 'modules\AgentBroker\Resources'
[void][IO.Directory]::CreateDirectory($directory)
$large = New-Object Drawing.Bitmap(128, 128, ([Drawing.Imaging.PixelFormat]::Format32bppArgb))
$icon = New-Object Drawing.Bitmap(32, 32, ([Drawing.Imaging.PixelFormat]::Format32bppArgb))
$graphics = [Drawing.Graphics]::FromImage($large)
$output = [Drawing.Graphics]::FromImage($icon)
$outline = New-Object Drawing.Pen([Drawing.Color]::FromArgb(255, 13, 40, 57), 16)
$edge = New-Object Drawing.Pen([Drawing.Color]::FromArgb(255, 87, 207, 221), 9)
$nodeOutline = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255, 13, 40, 57))
$broker = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255, 82, 224, 183))
$agent = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255, 97, 190, 255))
$shine = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(230, 238, 255, 255))
try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $outline.StartCap = $outline.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $edge.StartCap = $edge.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $points = @([Drawing.Point]::new(64, 29), [Drawing.Point]::new(28, 97), [Drawing.Point]::new(100, 97))
    foreach ($pen in @($outline, $edge)) {
        $graphics.DrawLine($pen, $points[0], $points[1])
        $graphics.DrawLine($pen, $points[0], $points[2])
        $graphics.DrawLine($pen, $points[1], $points[2])
    }
    for ($i = 0; $i -lt $points.Count; $i++) {
        $point = $points[$i]
        $graphics.FillEllipse($nodeOutline, $point.X - 23, $point.Y - 23, 46, 46)
        $brush = if ($i -eq 0) { $broker } else { $agent }
        $graphics.FillEllipse($brush, $point.X - 17, $point.Y - 17, 34, 34)
        $graphics.FillEllipse($shine, $point.X - 8, $point.Y - 9, 9, 9)
    }
    $output.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
    $output.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $output.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $output.DrawImage($large, 0, 0, 32, 32)
    $icon.Save((Join-Path $directory 'agentbroker.png'), [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    foreach ($resource in @($shine, $agent, $broker, $nodeOutline, $edge, $outline, $output, $graphics, $icon, $large)) {
        $resource.Dispose()
    }
}
Write-Host 'AgentBroker tray icon generated: modules/AgentBroker/Resources/agentbroker.png (32x32, transparent).'
