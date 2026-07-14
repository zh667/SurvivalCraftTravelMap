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

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-TeleportPerson {
    param(
        [Drawing.Graphics]$Graphics,
        [float]$X,
        [float]$Y,
        [Drawing.Color]$Color
    )

    $brush = [Drawing.SolidBrush]::new($Color)
    $body = New-RoundedRectanglePath -X $X -Y ($Y + 10) -Width 13 -Height 15 -Radius 4
    try {
        $Graphics.FillEllipse($brush, $X + 3, $Y, 7, 7)
        $Graphics.FillPath($brush, $body)
    }
    finally {
        $body.Dispose()
        $brush.Dispose()
    }
}

function Draw-TeleportButton {
    param(
        [Drawing.Graphics]$Graphics,
        [switch]$Pressed
    )

    $stoneLight = if ($Pressed) {
        [Drawing.Color]::FromArgb(255, 137, 133, 124)
    } else {
        [Drawing.Color]::FromArgb(255, 190, 184, 171)
    }
    $stoneMid = [Drawing.Color]::FromArgb(255, 155, 150, 139)
    $stoneShadow = [Drawing.Color]::FromArgb(255, 82, 79, 74)
    $panel = if ($Pressed) {
        [Drawing.Color]::FromArgb(255, 42, 44, 41)
    } else {
        [Drawing.Color]::FromArgb(255, 50, 52, 48)
    }
    $personColor = if ($Pressed) {
        [Drawing.Color]::FromArgb(255, 184, 181, 170)
    } else {
        [Drawing.Color]::FromArgb(255, 224, 220, 208)
    }
    $accentColor = if ($Pressed) {
        [Drawing.Color]::FromArgb(255, 86, 102, 56)
    } else {
        [Drawing.Color]::FromArgb(255, 116, 133, 70)
    }
    $offset = if ($Pressed) { 1 } else { 0 }

    $outer = New-RoundedRectanglePath -X 2 -Y 2 -Width 60 -Height 60 -Radius 8
    $bevel = New-RoundedRectanglePath -X 4 -Y 4 -Width 56 -Height 56 -Radius 7
    $center = New-RoundedRectanglePath -X 8 -Y 8 -Width 48 -Height 48 -Radius 5
    $outerBrush = [Drawing.SolidBrush]::new($stoneShadow)
    $bevelBrush = [Drawing.SolidBrush]::new($stoneMid)
    $centerBrush = [Drawing.SolidBrush]::new($panel)
    $highlight = [Drawing.Pen]::new($stoneLight, 2)
    $transfer = [Drawing.Pen]::new($accentColor, 2)
    $transfer.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $transfer.EndCap = [Drawing.Drawing2D.LineCap]::Triangle
    try {
        $Graphics.FillPath($outerBrush, $outer)
        $Graphics.FillPath($bevelBrush, $bevel)
        $Graphics.FillPath($centerBrush, $center)
        if ($Pressed) {
            $Graphics.DrawLine($highlight, 10, 54, 53, 54)
        } else {
            $Graphics.DrawLine($highlight, 10, 9, 53, 9)
        }

        Draw-TeleportPerson -Graphics $Graphics -X (14 + $offset) -Y (19 + $offset) -Color $personColor
        Draw-TeleportPerson -Graphics $Graphics -X (37 + $offset) -Y (19 + $offset) -Color $personColor
        $Graphics.DrawLine($transfer, 27 + $offset, 28 + $offset, 37 + $offset, 28 + $offset)
        $Graphics.DrawLine($transfer, 37 + $offset, 38 + $offset, 27 + $offset, 38 + $offset)
    }
    finally {
        $outer.Dispose(); $bevel.Dispose(); $center.Dispose()
        $outerBrush.Dispose(); $bevelBrush.Dispose(); $centerBrush.Dispose()
        $highlight.Dispose(); $transfer.Dispose()
    }
}

New-TravelMapIcon -Name "TeleportButton.png" -Draw {
    param($graphics)
    Draw-TeleportButton -Graphics $graphics
}

New-TravelMapIcon -Name "TeleportButton_Pressed.png" -Draw {
    param($graphics)
    Draw-TeleportButton -Graphics $graphics -Pressed
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
