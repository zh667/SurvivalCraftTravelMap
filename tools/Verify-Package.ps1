param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$PackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$expectedPackageName = "SurvivalcraftTravelMap.netmod"
if ([IO.Path]::GetFileName($resolvedPackage) -cne $expectedPackageName) {
    throw "Package filename must be exactly '$expectedPackageName'."
}

$requiredEntries = @(
    "SurvivalcraftTravelMap.dll",
    "modinfo.json",
    "mod.netxdb",
    "Assets/BlockPixelColor.json",
    "Assets/Point.png",
    "Assets/TeleportButton.png",
    "Assets/TeleportButton_Pressed.png",
    "Assets/TeleportTo.png",
    "Assets/Lang/zh-CN.json",
    "Assets/Lang/en-US.json",
    "Assets/Lang/es-MX.json",
    "Assets/Lang/pt-BR.json",
    "Assets/Lang/ru-RU.json"
)
$gameDlls = @(
    "Survivalcraft.dll",
    "Engine.dll",
    "EntitySystem.dll",
    "Newtonsoft.Json.dll",
    "LiteNetLib.dll"
)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$entryBytes = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
$maximumEntryBytes = 8L * 1024L * 1024L
$maximumAggregateBytes = 16L * 1024L * 1024L
$declaredAggregateBytes = 0L
$copiedAggregateBytes = 0L

$packageStream = [IO.File]::OpenRead($resolvedPackage)
$archive = $null
try {
    $archive = [IO.Compression.ZipArchive]::new(
        $packageStream,
        [IO.Compression.ZipArchiveMode]::Read,
        $false)

    foreach ($entry in $archive.Entries) {
        $entryName = $entry.FullName
        if (-not $seen.Add($entryName)) {
            throw "Package contains duplicate entry '$entryName'."
        }
        if ($entryName.Contains("\")) {
            throw "Package entry '$entryName' is not a stable relative path."
        }

        $segments = $entryName.Split("/")
        if ($entryName.StartsWith("/", [StringComparison]::Ordinal) -or
            $segments.Count -eq 0 -or
            $segments.Where({ [string]::IsNullOrEmpty($_) -or $_ -eq "." -or $_ -eq ".." }).Count -ne 0) {
            throw "Package entry '$entryName' is not a stable relative path."
        }

        $leafName = $segments[$segments.Count - 1]
        if ($gameDlls -contains $leafName) {
            throw "Package contains forbidden game DLL '$entryName'."
        }

        if ($requiredEntries -cnotcontains $entryName) {
            throw "Package entry '$entryName' is outside the package allowlist."
        }

        $entryLength = [long]$entry.Length
        if ($entryLength -gt $maximumEntryBytes) {
            throw "Package entry '$entryName' exceeds the uncompressed size limit."
        }
        if ($declaredAggregateBytes -gt ($maximumAggregateBytes - $entryLength)) {
            throw "Package exceeds the aggregate uncompressed size limit."
        }
        $declaredAggregateBytes += $entryLength

        $entryStream = $entry.Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(81920)
            $entryCopiedBytes = 0L
            while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                if ($entryCopiedBytes -gt ($maximumEntryBytes - $read)) {
                    throw "Package entry '$entryName' exceeds the uncompressed size limit while reading."
                }
                if ($copiedAggregateBytes -gt ($maximumAggregateBytes - $read)) {
                    throw "Package exceeds the aggregate uncompressed size limit while reading."
                }

                $memory.Write($buffer, 0, $read)
                $entryCopiedBytes += $read
                $copiedAggregateBytes += $read
            }
            $bytes = $memory.ToArray()
        }
        finally {
            $memory.Dispose()
            $entryStream.Dispose()
        }

        $entryBytes.Add($entryName, $bytes)

        $content = [Text.Encoding]::UTF8.GetString($bytes) + "`n" + [Text.Encoding]::Unicode.GetString($bytes)
        if ([regex]::IsMatch($content, '(?i)\b\d+\.\d+\.\d+(?:\.\d+)?\+[0-9a-f]{40}\b')) {
            throw "Package contains a forbidden SDK source revision string in '$entryName'."
        }
        if ($content.Contains("AntiCheatReportPackage")) {
            throw "Package contains forbidden AntiCheatReportPackage string in '$entryName'."
        }
        if ([regex]::IsMatch($content, '(?i)\bPackage\s*Id\s*(?:[:=]|=>)?\s*["'']?60\b')) {
            throw "Package contains forbidden package ID 60 string in '$entryName'."
        }
        foreach ($marker in @("ReadOnlyModList", "CheckDataBaseValid", "181215270", "Setting.png")) {
            if ($content.Contains($marker)) {
                throw "Package contains forbidden verification or obsolete resource marker '$marker' in '$entryName'."
            }
        }
    }

    foreach ($requiredEntry in $requiredEntries) {
        if (-not $seen.Contains($requiredEntry)) {
            throw "Package is missing required entry '$requiredEntry'."
        }
    }

    $dllText = [Text.Encoding]::UTF8.GetString($entryBytes["SurvivalcraftTravelMap.dll"]) +
        "`n" + [Text.Encoding]::Unicode.GetString($entryBytes["SurvivalcraftTravelMap.dll"])
    if (-not $dllText.Contains("AssemblyInformationalVersionAttribute") -or
        -not $dllText.Contains("1.0.0")) {
        throw "Package DLL must expose stable informational version '1.0.0'."
    }


    $manifestText = [Text.Encoding]::UTF8.GetString($entryBytes["modinfo.json"])
    $manifest = $manifestText | ConvertFrom-Json
    if ($manifest.Name -cne "Survivalcraft Travel Map" -or
        $manifest.Author -cne "SCTM" -or
        $manifest.PackageName -cne "SurvivalcraftTravelMap" -or
        $manifest.ApiVersion -cne "1.44" -or
        $manifest.ScVersion -cne "2.4.40.6" -or
        $manifest.Dependencies.Count -ne 0) {
        throw "Package manifest identity is invalid."
    }

    [xml]$xdb = [Text.Encoding]::UTF8.GetString($entryBytes["mod.netxdb"])
    $members = @($xdb.SurvivalCraftMap.EntityTemplate.MemberComponentTemplate)
    $components = @($xdb.SurvivalCraftMap.Folder.ComponentTemplate)
    if ($xdb.SurvivalCraftMap.EntityTemplate.Name -cne "Player" -or
        $xdb.SurvivalCraftMap.EntityTemplate.Guid -cne "4be6c1c5-d65d-4537-8a8b-a391969e6dc2" -or
        $members.Count -ne 1 -or
        $members[0].Name -cne "TravelMap" -or
        $members[0].Guid -cne "32be124c-0f5b-4ca0-ae58-df7fa2b707d3" -or
        $members[0].InheritanceParent -cne "4b67335f-9888-4824-9f0e-cc5f72204b8e" -or
        $xdb.SurvivalCraftMap.Folder.Name -cne "Gameplay" -or
        $xdb.SurvivalCraftMap.Folder.Guid -cne "d3d4b692-acc9-4128-9b99-a5acf1de1fbb" -or
        $components.Count -ne 1 -or
        $components[0].Name -cne "TravelMap" -or
        $components[0].Guid -cne "4b67335f-9888-4824-9f0e-cc5f72204b8e" -or
        $components[0].InheritanceParent -cne "b05700ed-7e4e-4679-98f5-b597f421496b" -or
        $components[0].Parameter.Name -cne "Class" -or
        $components[0].Parameter.Guid -cne "e14340ef-ab75-4dbe-aad2-9b08f7b7b61a" -or
        $components[0].Parameter.Value -cne "SurvivalcraftTravelMap.Mod.TravelMapComponent" -or
        $components[0].Parameter.Type -cne "string") {
        throw "Package XDB must inject exactly one TravelMapComponent."
    }

    $colorText = [Text.Encoding]::UTF8.GetString($entryBytes["Assets/BlockPixelColor.json"])
    $colors = $colorText | ConvertFrom-Json
    if (@($colors.PSObject.Properties).Count -ne 257) {
        throw "Assets/BlockPixelColor.json must contain exactly 257 entries."
    }

    $pngSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    foreach ($pngName in $requiredEntries.Where({ $_.EndsWith(".png", [StringComparison]::Ordinal) })) {
        $bytes = $entryBytes[$pngName]
        if ($bytes.Length -lt 24) {
            throw "Required PNG '$pngName' is truncated."
        }
        for ($index = 0; $index -lt $pngSignature.Length; $index++) {
            if ($bytes[$index] -ne $pngSignature[$index]) {
                throw "Required PNG '$pngName' has an invalid signature."
            }
        }
        if ([Text.Encoding]::ASCII.GetString($bytes, 12, 4) -cne "IHDR") {
            throw "Required PNG '$pngName' has no IHDR header."
        }
        $width = [uint32]$bytes[16] -shl 24 -bor [uint32]$bytes[17] -shl 16 -bor [uint32]$bytes[18] -shl 8 -bor [uint32]$bytes[19]
        $height = [uint32]$bytes[20] -shl 24 -bor [uint32]$bytes[21] -shl 16 -bor [uint32]$bytes[22] -shl 8 -bor [uint32]$bytes[23]
        if ($width -eq 0 -or $height -eq 0) {
            throw "Required PNG '$pngName' has invalid dimensions."
        }
    }
}
finally {
    if ($null -ne $archive) {
        $archive.Dispose()
    }
    $packageStream.Dispose()
}

Write-Output "PACKAGE_OK"
