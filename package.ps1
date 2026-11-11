param(
    [Parameter(Mandatory = $true)][string]$GameDirectory
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -GameDirectory $GameDirectory
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/WitchSpring3Resolution/Plugin.cs') -Raw
if ($source -notmatch '\[BepInPlugin\(Id,\s*"[^"]+",\s*"([0-9.]+)"\)\]') { throw 'Plugin version not found.' }
$version = $Matches[1]
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist ('package-' + [Guid]::NewGuid().ToString('N'))
$plugins = Join-Path $stage 'BepInEx/plugins/WitchSpring3Resolution'
$config = Join-Path $stage 'BepInEx/config'
New-Item -ItemType Directory -Force -Path $plugins,$config | Out-Null
Copy-Item -LiteralPath (Join-Path $dist 'WitchSpring3Resolution.dll') -Destination $plugins
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config/com.dsh.witchspring3.resolution.cfg') -Destination $config
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $stage
$zip = Join-Path $dist "WitchSpring3Resolution-v$version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value ($hash + '  ' + (Split-Path $zip -Leaf)) -Encoding ascii
Get-Item -LiteralPath $zip,"$zip.sha256" | Select-Object FullName,Length
