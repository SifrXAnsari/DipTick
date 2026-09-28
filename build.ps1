<#
.SYNOPSIS
    Builds DipTick: out\diptick.exe, one self-contained file, and out\DipTick-<version>-x64.msi, the
    installer that puts it in Program Files.

.DESCRIPTION
    Two steps, in this order:

      1. The .NET SDK publishes src\MVVMC for 64-bit Windows as one file: the program, the .NET
         runtime, and the native libraries it needs (Skia and HarfBuzz for drawing, ANGLE, SQLite,
         Oniguruma), which it unpacks to a folder of its own the first time it starts. Every
         compiler and analyzer warning is an error (src\MVVMC\DipTick.csproj).
      2. The WiX Toolset's MSBuild SDK builds installer\DipTick.wixproj around it, with LICENSE and
         THIRD-PARTY-NOTICES.txt beside it. WiX 7 runs only once its licence, the Open Source
         Maintenance Fee EULA (docs.firegiant.com/wix/osmf/), is accepted: -AcceptWixEula accepts it
         for this build and stores nothing. Without it, the exe alone is built.

    Nothing else is needed to install or run DipTick: the MSI alone installs it.

    The version is the project's own: Version in src\MVVMC\DipTick.csproj.

.PARAMETER AcceptWixEula
    Accept the WiX Toolset's licence for this build, and build the MSI too.

.PARAMETER NoInstaller
    Build the exe alone.
#>
[CmdletBinding()]
param(
    [switch] $AcceptWixEula,
    [switch] $NoInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$out = Join-Path $root 'out'
$project = Join-Path $root 'src\MVVMC\DipTick.csproj'
$version = ([xml] [System.IO.File]::ReadAllText($project)).SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $version -or -not $version.InnerText) {
    throw 'src\MVVMC\DipTick.csproj names no Version.'
}
$version = $version.InnerText

# ---------------------------------------------------------------- 1. the exe
$publish = Join-Path $out 'publish'
& dotnet publish $project -c Release -r win-x64 --self-contained true -nologo -o $publish
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)."
}
$exe = Join-Path $publish 'diptick.exe'
$others = @(Get-ChildItem -LiteralPath $publish -File | Where-Object Name -ne 'diptick.exe')
if ($others.Count -gt 0) {
    throw "The publish left files beside diptick.exe: $(($others | ForEach-Object Name) -join ', ')."
}
Copy-Item -LiteralPath $exe -Destination (Join-Path $out 'diptick.exe') -Force
Write-Host ("out\diptick.exe: DipTick {0}, {1:N1} MB" -f $version, ((Get-Item -LiteralPath $exe).Length / 1MB))

if ($NoInstaller) {
    return
}
if (-not $AcceptWixEula) {
    Write-Host 'The MSI was not built: WiX 7 needs its licence accepted. Run .\build.ps1 -AcceptWixEula to accept it for this build.'
    return
}

# ---------------------------------------------------------------- 2. the MSI
$payload = Join-Path $out 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $payload 'diptick.exe') -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $payload 'THIRD-PARTY-NOTICES.txt') -Force

$installer = Join-Path $root 'installer\DipTick.wixproj'
& dotnet build $installer -c Release -nologo "-p:PayloadDir=$payload" "-p:DipTickVersion=$version" -p:AcceptEula=wix7
if ($LASTEXITCODE -ne 0) {
    throw "The installer's build failed (exit $LASTEXITCODE)."
}
$msiName = "DipTick-$version-x64.msi"
$msi = Join-Path $root "installer\bin\x64\Release\$msiName"
if (-not (Test-Path -LiteralPath $msi)) {
    $msi = Get-ChildItem -LiteralPath (Join-Path $root 'installer\bin') -Recurse -Filter $msiName | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $msi) {
    throw "The installer's build wrote no $msiName."
}
Copy-Item -LiteralPath $msi -Destination (Join-Path $out $msiName) -Force
Write-Host ("out\{0}: {1:N1} MB" -f $msiName, ((Get-Item -LiteralPath (Join-Path $out $msiName)).Length / 1MB))
