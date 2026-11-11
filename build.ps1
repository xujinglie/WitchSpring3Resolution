param(
    [Parameter(Mandatory = $true)][string]$GameDirectory
)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$managed = Join-Path $gameRoot 'WitchSpring3 ReFine_Data/Managed'
$core = Join-Path $gameRoot 'BepInEx/core'
foreach ($required in @($managed, (Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing dependency: $required" }
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkLines = & $dotnet --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'Unable to list installed .NET SDKs.' }
$sdkDirs = foreach ($line in $sdkLines) {
    if ($line -match '^([^ ]+) \[(.+)\]$') {
        $candidate = Join-Path $Matches[2] $Matches[1]
        if (Test-Path -LiteralPath (Join-Path $candidate 'Roslyn/bincore/csc.dll')) { $candidate }
    }
}
$sdkDir = @($sdkDirs) | Select-Object -Last 1
if (-not $sdkDir) { throw 'No .NET SDK with the Roslyn C# compiler was found.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compilerArgs = @((Join-Path $sdkDir 'Roslyn/bincore/csc.dll'), '-nologo', '-noconfig', '-nostdlib+', '-target:library', '-langversion:7.3', '-codepage:65001', '-optimize+', '-deterministic', ('-out:' + (Join-Path $output 'WitchSpring3Resolution.dll')))
$refs = @((Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll'))
$refs += Get-ChildItem -LiteralPath $managed -Filter '*.dll' | Select-Object -ExpandProperty FullName
foreach ($reference in $refs) { $compilerArgs += '-r:' + $reference }
$compilerArgs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src/WitchSpring3Resolution') -Recurse -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $dotnet @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Get-Item -LiteralPath (Join-Path $output 'WitchSpring3Resolution.dll') | Select-Object FullName,Length
