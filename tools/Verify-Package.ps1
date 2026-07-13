param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$PackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$requiredEntries = @(
    "SurvivalcraftTravelMap.dll",
    "modinfo.json",
    "mod.netxdb"
)
$gameDlls = @(
    "Survivalcraft.dll",
    "Engine.dll",
    "EntitySystem.dll",
    "Newtonsoft.Json.dll",
    "LiteNetLib.dll"
)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

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

        $isRequiredRootEntry = $requiredEntries -contains $entryName
        $isAsset = $entryName.StartsWith("Assets/", [StringComparison]::Ordinal) -and $segments.Count -gt 1
        if (-not $isRequiredRootEntry -and -not $isAsset) {
            throw "Package entry '$entryName' is outside the package allowlist."
        }

        $entryStream = $entry.Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $entryStream.CopyTo($memory)
            $bytes = $memory.ToArray()
        }
        finally {
            $memory.Dispose()
            $entryStream.Dispose()
        }

        $content = [Text.Encoding]::UTF8.GetString($bytes) + "`n" + [Text.Encoding]::Unicode.GetString($bytes)
        if ($content.Contains("AntiCheatReportPackage")) {
            throw "Package contains forbidden AntiCheatReportPackage string in '$entryName'."
        }
        if ([regex]::IsMatch($content, '(?i)\bPackage\s*Id\s*(?:[:=]|=>)?\s*["'']?60\b')) {
            throw "Package contains forbidden package ID 60 string in '$entryName'."
        }
    }

    foreach ($requiredEntry in $requiredEntries) {
        if (-not $seen.Contains($requiredEntry)) {
            throw "Package is missing required entry '$requiredEntry'."
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
