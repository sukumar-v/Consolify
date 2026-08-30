<#
.SYNOPSIS
    Turns raw app screenshots into the captioned images used in the README.

.DESCRIPTION
    Scales each shot to 1600px wide, then frames it in the app's own palette: a hairline border
    so the light screenshots do not bleed into GitHub's white page, an accent rule, and a caption
    bar underneath saying what the picture shows. Re-run it after retaking a screenshot -- the
    captions live in $Shots below, next to the file they belong to.

.EXAMPLE
    .\tools\caption-screenshots.ps1 -SourceDir .\raw-screenshots
#>
[CmdletBinding()]
param(
    [string]$SourceDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\raw'),
    [string]$OutDir    = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'),
    [int]$Width        = 1600
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# source file -> output name + the line printed under it
$Shots = @(
    @{ In = '1_Library_screen_with_all_your_games.png';               Out = 'screenshot-library.png'
       Caption = 'Every game you own in one place - Steam, Epic, GOG and Xbox' },
    @{ In = '2_Costomizable_Settings.png';                            Out = 'screenshot-settings.png'
       Caption = 'Tune every control, display and behaviour from the couch' },
    @{ In = '3_Mouse_and_keyboard_controls_through_gamepad.png';      Out = 'screenshot-keyboard.png'
       Caption = 'Browse, type and click - the gamepad is your mouse and keyboard' },
    @{ In = '4_Power_wheel_gives_you_more_control.png';               Out = 'screenshot-power-wheel.png'
       Caption = 'The Power Wheel: switch windows, run shortcuts, blank the TV' }
)

$base   = [System.Drawing.Color]::FromArgb(0x08, 0x08, 0x0A)   # app Base
$accent = [System.Drawing.Color]::FromArgb(0xF0, 0xA2, 0x53)   # app Accent
$edge   = [System.Drawing.Color]::FromArgb(0x2A, 0x2A, 0x30)
$ink    = [System.Drawing.Color]::FromArgb(0xF2, 0xF2, 0xF5)

$barHeight = 96
$ruleHeight = 3

foreach ($shot in $Shots) {
    $inPath = Join-Path $SourceDir $shot.In
    if (-not (Test-Path $inPath)) { Write-Warning "Skipping missing $($shot.In)"; continue }

    $src = [System.Drawing.Image]::FromFile($inPath)
    try {
        $h = [int][Math]::Round($Width * $src.Height / $src.Width)
        $canvas = New-Object System.Drawing.Bitmap($Width, ($h + $ruleHeight + $barHeight))
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $g.InterpolationMode  = 'HighQualityBicubic'
            $g.SmoothingMode      = 'AntiAlias'
            $g.TextRenderingHint  = 'AntiAliasGridFit'
            $g.Clear($base)
            $g.DrawImage($src, 0, 0, $Width, $h)

            $g.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), 0, $h, $Width, $ruleHeight)

            $font = New-Object System.Drawing.Font('Segoe UI Semibold', 20, [System.Drawing.FontStyle]::Regular,
                                                   [System.Drawing.GraphicsUnit]::Pixel)
            # Shrink rather than wrap: these are one-liners, and a wrapped caption in a fixed-height
            # bar would clip instead of just looking tight.
            while ($g.MeasureString($shot.Caption, $font).Width -gt ($Width - 120) -and $font.Size -gt 12) {
                $smaller = New-Object System.Drawing.Font($font.FontFamily, ($font.Size - 1),
                                                          $font.Style, [System.Drawing.GraphicsUnit]::Pixel)
                $font.Dispose(); $font = $smaller
            }
            $fmt = New-Object System.Drawing.StringFormat
            $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
            $g.DrawString($shot.Caption, $font, (New-Object System.Drawing.SolidBrush($ink)),
                          (New-Object System.Drawing.RectangleF(60, ($h + $ruleHeight), ($Width - 120), $barHeight)), $fmt)
            $font.Dispose()

            $g.DrawRectangle((New-Object System.Drawing.Pen($edge, 1)), 0, 0, ($Width - 1), ($canvas.Height - 1))
        } finally { $g.Dispose() }

        $outPath = Join-Path $OutDir $shot.Out
        $canvas.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)

        # Flat UI stays PNG, but a shot that is mostly cover art does not compress and lands around
        # 2 MB -- too much to make every reader of the README download. Those go to JPEG instead.
        if ((Get-Item $outPath).Length -gt 700KB) {
            Remove-Item $outPath -Force
            $outPath = [IO.Path]::ChangeExtension($outPath, ".jpg")
            $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
                     Where-Object { $_.MimeType -eq "image/jpeg" }
            $params = New-Object System.Drawing.Imaging.EncoderParameters(1)
            $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
                [System.Drawing.Imaging.Encoder]::Quality, 92L)
            $canvas.Save($outPath, $codec, $params)
        }
        $canvas.Dispose()
        '{0,-30} {1}x{2}  {3:N0} KB' -f (Split-Path $outPath -Leaf), $Width, ($h + $ruleHeight + $barHeight), ((Get-Item $outPath).Length / 1KB)
    } finally { $src.Dispose() }
}
