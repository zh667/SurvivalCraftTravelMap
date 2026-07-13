param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\src\SurvivalcraftTravelMap\Assets")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$basalt = [Drawing.Color]::FromArgb(255, 27, 38, 40)
$moss = [Drawing.Color]::FromArgb(255, 111, 138, 59)
$cyan = [Drawing.Color]::FromArgb(255, 116, 201, 200)
$amber = [Drawing.Color]::FromArgb(255, 226, 163, 59)
$snow = [Drawing.Color]::FromArgb(255, 232, 236, 231)

function New-TravelMapIcon {
    param(
        [string]$Name,
        [scriptblock]$Draw
    )

    $bitmap = [Drawing.Bitmap]::new(64, 64, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)
        & $Draw $graphics
        $path = Join-Path $outputRoot $Name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-TravelMapIcon -Name "Point.png" -Draw {
    param($graphics)
    $outer = [Drawing.SolidBrush]::new($amber)
    $inner = [Drawing.SolidBrush]::new($basalt)
    $center = [Drawing.SolidBrush]::new($cyan)
    try {
        $graphics.FillEllipse($outer, 8, 8, 48, 48)
        $graphics.FillEllipse($inner, 15, 15, 34, 34)
        $graphics.FillEllipse($center, 25, 25, 14, 14)
    }
    finally {
        $outer.Dispose(); $inner.Dispose(); $center.Dispose()
    }
}

function Draw-TeleportArrow {
    param($Graphics, [Drawing.Color]$Background, [Drawing.Color]$Foreground)
    $backgroundBrush = [Drawing.SolidBrush]::new($Background)
    $foregroundBrush = [Drawing.SolidBrush]::new($Foreground)
    $border = [Drawing.Pen]::new($moss, 3)
    try {
        $Graphics.FillRectangle($backgroundBrush, 4, 4, 56, 56)
        $Graphics.DrawRectangle($border, 5, 5, 53, 53)
        $points = [Drawing.Point[]]@(
            [Drawing.Point]::new(14, 27),
            [Drawing.Point]::new(38, 27),
            [Drawing.Point]::new(38, 17),
            [Drawing.Point]::new(53, 32),
            [Drawing.Point]::new(38, 47),
            [Drawing.Point]::new(38, 37),
            [Drawing.Point]::new(14, 37))
        $Graphics.FillPolygon($foregroundBrush, $points)
    }
    finally {
        $backgroundBrush.Dispose(); $foregroundBrush.Dispose(); $border.Dispose()
    }
}

New-TravelMapIcon -Name "TeleportButton.png" -Draw {
    param($graphics)
    Draw-TeleportArrow -Graphics $graphics -Background $basalt -Foreground $cyan
}

New-TravelMapIcon -Name "TeleportButton_Pressed.png" -Draw {
    param($graphics)
    Draw-TeleportArrow -Graphics $graphics -Background $moss -Foreground $snow
}

New-TravelMapIcon -Name "TeleportTo.png" -Draw {
    param($graphics)
    $pin = [Drawing.SolidBrush]::new($amber)
    $hole = [Drawing.SolidBrush]::new($basalt)
    $route = [Drawing.Pen]::new($cyan, 5)
    try {
        $graphics.FillEllipse($pin, 27, 5, 30, 30)
        $graphics.FillPolygon($pin, [Drawing.Point[]]@(
            [Drawing.Point]::new(30, 26),
            [Drawing.Point]::new(54, 26),
            [Drawing.Point]::new(42, 53)))
        $graphics.FillEllipse($hole, 36, 14, 12, 12)
        $route.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $route.EndCap = [Drawing.Drawing2D.LineCap]::ArrowAnchor
        $graphics.DrawLine($route, 7, 50, 30, 39)
    }
    finally {
        $pin.Dispose(); $hole.Dispose(); $route.Dispose()
    }
}

Write-Output "ASSETS_GENERATED $outputRoot"
