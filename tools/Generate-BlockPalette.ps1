param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\SurvivalcraftTravelMap\Assets\BlockPixelColor.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# RGBA bytes for block IDs 0..256. The colors follow the restrained terrain
# style of the user-supplied custom-minimap reference. Air remains transparent
# for this mod's exploration contract; the remaining entries keep the reference
# colors, including the translucent glass entry at ID 57.
$paletteBytes = [Convert]::FromBase64String(
    "AAAAAGFhYf+Eajr/gICA/7mrgP98fHz/g3t6/9PElf+rq6v/xppT/8aaU//GmlP/oKCg/6CgoP94eHj//////5KSkv94XDL/qKio/6urq/+0AAD/h2U3/yVLCf/+/v7/cD/2//////+Pj4//h2U3/0o1F//+/v7/mpqa/+3Zlf+ampr/mpqa/5qamv+ampr/Q0ND//////8hISH/iIiI/yIiIv+JiYn//v7+/yVLCf//////QCoS/9HR0f+MQCH/fHx8/4dlN/+Pj4//uauA/7mrgP98fHz/j4+P/4dlN/+HZTf/paWlpXZ2dv9ePRr//////+709P/Y4vP/bkYe/3x8fP98fHz/iYmJ/3R0dP/WxrH/1sax/9bGsf8yyqb/n52V/4c9M/9AIxL/hz0z/4c9M/8lSwn/JUsJ/yVLCf/+/v7//v7+//7+/v92VzD/hoaG/+709P+HZTf/mpqa/7FVUP+xVVD/39/f/9/f3//peh3/39/f/4dlN/90dHT/dHR0/4dlN/+HZTf/ZFI8/8G0jv90dHT//Pz8/yVLCf//MjL/h2U3/4dlN/+HZTf//v7+/2BiO//f39///wAA/3R0dP//////JUsJ/3BKJP/+/v7/Xj0a//z8/P88aQD/39/f/9/f3//+/v7//v7+//7+/v/+/v7/WKf//1x0NP/f39//39/f/+rq6v++eS3/vnkt/5xWNf9DQ0P/Q0ND/4CAgP9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/ppQA/3R0dP8lSwn/DAwM/0NDQ/9DQ0P/h2U3/4CAgP9DQ0P/Q0ND/0NDQ//k4+D/5OPg/9/f3/+vz8r/r8/K/3R0dP+HPTP//////4dlN//f39//f2U2/yVLCf+ampr//////yVLCf/+/v7/EUEW/+rq6v8lSwn/k2g3/4dlN/9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/Q0ND/0NDQ/9DQ0P/gICA/4CAgP/+/v7//v7+/8DAwP/AwMD/JUsJ/yIiIv8RcCr//v7+///////+/v7/JUsJ/4CAgP/+/v7///////7+/v/k4+D/LB8J/9LOw/8MDAz/39/f/9/f3//+/v7/39/f/2dnZ/8lSwn/QCoS/4CAgP8hISH///////7+/v/+/v7/Q0ND/4c9M/9DQ0P/eHh4/7CwsP+HZTf/JUsJ/7CwsP//////Q0ND/7CwsP+wsLD/h2U3/4dlN/+HZTf/Q0ND/3BKJP8lSwn/sVVQ/6/Pyv+TaDf/fHx8/21kQv/f39///Pz8/yVLCf+ampr/JUsJ/yVLCf/f39//39/f/0NDQ/9DQ0P/xppT/3h4eP8=")
if ($paletteBytes.Length -ne (257 * 4)) {
    throw "Terrain palette must contain exactly 257 RGBA entries."
}

$environmentSensitive = [Collections.Generic.HashSet[int]]::new()
foreach ($index in @(8, 12, 13, 14, 18, 19, 225, 256)) {
    [void]$environmentSensitive.Add($index)
}

$document = [ordered]@{}
foreach ($index in 0..256) {
    $offset = $index * 4
    $rgba = @(
        $paletteBytes[$offset],
        $paletteBytes[$offset + 1],
        $paletteBytes[$offset + 2],
        $paletteBytes[$offset + 3]
    )

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
Write-Output "Generated reference-style block palette: $resolvedOutput"
