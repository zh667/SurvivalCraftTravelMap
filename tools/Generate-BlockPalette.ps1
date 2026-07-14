param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\SurvivalcraftTravelMap\Assets\BlockPixelColor.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# This palette is project-owned and generated without reading any existing palette.
# Known overrides use the public block Index constants exposed by the referenced
# Survivalcraft runtime. Remaining indices receive a deterministic, subdued HSV
# color so new or uncommon blocks remain distinguishable on the map.
function ConvertFrom-Hsv {
    param(
        [double]$Hue,
        [double]$Saturation,
        [double]$Value
    )

    $chroma = $Value * $Saturation
    $sector = $Hue / 60.0
    $x = $chroma * (1.0 - [Math]::Abs(($sector % 2.0) - 1.0))
    $red = 0.0
    $green = 0.0
    $blue = 0.0

    if ($sector -lt 1.0) { $red = $chroma; $green = $x }
    elseif ($sector -lt 2.0) { $red = $x; $green = $chroma }
    elseif ($sector -lt 3.0) { $green = $chroma; $blue = $x }
    elseif ($sector -lt 4.0) { $green = $x; $blue = $chroma }
    elseif ($sector -lt 5.0) { $red = $x; $blue = $chroma }
    else { $red = $chroma; $blue = $x }

    $match = $Value - $chroma
    @(
        [int][Math]::Round(($red + $match) * 255.0),
        [int][Math]::Round(($green + $match) * 255.0),
        [int][Math]::Round(($blue + $match) * 255.0),
        255
    )
}

function Get-GeneratedColor {
    param([int]$Index)

    $hue = ($Index * 47) % 360
    $saturation = 0.24 + ((($Index * 13) % 5) * 0.035)
    $value = 0.44 + ((($Index * 7) % 6) * 0.035)
    ConvertFrom-Hsv -Hue $hue -Saturation $saturation -Value $value
}

# Air, common terrain, ores, liquids and vegetation get recognizable colors.
# IDs 8, 12, 13, 14, 18, 225 and 256 are white because TerrainMapSampler
# multiplies them by the game's temperature/humidity environment palette.
$knownColors = @{
    0   = @(0, 0, 0, 0)          # Air
    1   = @(112, 106, 99, 255)   # Stone
    2   = @(126, 91, 58, 255)    # Dirt
    3   = @(132, 126, 121, 255)  # Granite
    7   = @(215, 195, 139, 255)  # Sand
    8   = @(255, 255, 255, 255)  # Grass (environment tint)
    9   = @(131, 91, 52, 255)    # Oak wood
    12  = @(255, 255, 255, 255)  # Leaves (environment tint)
    13  = @(255, 255, 255, 255)  # Leaves (environment tint)
    14  = @(255, 255, 255, 255)  # Leaves (environment tint)
    16  = @(58, 58, 55, 255)     # Coal ore
    18  = @(255, 255, 255, 255)  # Water (environment tint)
    39  = @(142, 124, 103, 255)  # Iron ore
    41  = @(169, 102, 65, 255)   # Copper ore
    61  = @(238, 242, 244, 255)  # Snow
    62  = @(168, 217, 228, 255)  # Ice
    66  = @(205, 198, 166, 255)  # Limestone
    67  = @(55, 62, 64, 255)     # Basalt
    92  = @(238, 85, 30, 255)    # Magma
    104 = @(244, 143, 35, 255)   # Fire
    112 = @(74, 191, 196, 255)   # Diamond ore
    127 = @(52, 122, 65, 255)    # Cactus
    225 = @(255, 255, 255, 255)  # Environment-tinted vegetation
    226 = @(47, 112, 154, 255)   # Water variant
    229 = @(47, 112, 154, 255)   # Water variant
    232 = @(47, 112, 154, 255)   # Water variant
    233 = @(47, 112, 154, 255)   # Water variant
    256 = @(255, 255, 255, 255)  # Environment-tinted vegetation
}

$environmentSensitive = [Collections.Generic.HashSet[int]]::new()
foreach ($index in @(8, 12, 13, 14, 18, 225, 256)) {
    [void]$environmentSensitive.Add($index)
}

$document = [ordered]@{}
foreach ($index in 0..256) {
    $rgba = if ($knownColors.ContainsKey($index)) {
        $knownColors[$index]
    }
    else {
        Get-GeneratedColor -Index $index
    }

    $packedValue = [uint64]$rgba[0] `
        -bor ([uint64]$rgba[1] -shl 8) `
        -bor ([uint64]$rgba[2] -shl 16) `
        -bor ([uint64]$rgba[3] -shl 24)
    $document[$index.ToString([Globalization.CultureInfo]::InvariantCulture)] = [ordered]@{
        BlockIndex = $index
        Color = [ordered]@{
            PackedValue = $packedValue
            R = $rgba[0]
            G = $rgba[1]
            B = $rgba[2]
            A = $rgba[3]
        }
        NeedChangeWithEnvironment = $environmentSensitive.Contains($index)
    }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [string]::IsNullOrEmpty($outputDirectory)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$json = $document | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($resolvedOutput, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Generated independent block palette: $resolvedOutput"
