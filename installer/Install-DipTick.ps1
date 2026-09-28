<#
.SYNOPSIS
    Installs DipTick: first Microsoft's Visual C++ runtime, fetched from Microsoft, when this PC has
    none new enough, then the DipTick MSI beside this script.

.DESCRIPTION
    diptick.exe carries libsodium, which needs Microsoft's Visual C++ v14 runtime for 64-bit Windows,
    version 14.44 or newer. Windows does not always have it. This script:

      1. Looks for it where Microsoft's own installer records it ("Redistribute Visual C++ Files",
         Microsoft Learn): HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64, in the 64-bit
         and the 32-bit view of the registry.
      2. When it is missing or older, downloads Microsoft's installer for it, over HTTPS only, with
         Windows' own curl.exe: the exact file named below, which must match its SHA-512; or, when
         Microsoft no longer serves that file, the newest one from Microsoft's permanent address.
         Either way it must carry a valid signature from Microsoft Corporation, or nothing is run. It
         installs quietly, under Microsoft's own licence terms (https://aka.ms/VCRedistLicense), and
         the downloaded file is deleted.
      3. Installs the DipTick MSI in this script's folder.

    Installing needs an administrator: the script asks Windows to run it as one. Nothing is changed
    before that.

    Run it from a terminal:
        powershell -ExecutionPolicy Bypass -File .\Install-DipTick.ps1
    or with a right-click on it, Run with PowerShell.

.PARAMETER DryRun
    Install nothing: say what is found, and download and check Microsoft's installer, which is then
    deleted.
#>
[CmdletBinding()]
param(
    [switch] $DryRun,

    # Where the run that Windows starts as administrator writes what it says, for this window.
    [string] $Log
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# The signature and hash checks come from this PowerShell's own modules, named by path: a module path
# inherited from another PowerShell can name modules this one cannot load.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1')
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')

# ---------------------------------------------------------------- the runtime DipTick needs
$Needed = [version]'14.44'
$Exact = @{
    Url     = 'https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe'
    Sha512  = 'C42C82CFD5EAA2165B600F6109CB14FF4308CDF4CFF656A1375B4A933F1630BFE54C99C4D0FB9ED3BEA4DC4E4FDAA3DB8E7C58356CCA07C2954F4C297757D62D'
    Version = '14.51.36247.0'
}
$Newest = 'https://aka.ms/vc14/vc_redist.x64.exe'

function Say([string] $text) {
    Write-Host "DipTick setup: $text"
    if ($Log) {
        Add-Content -LiteralPath $Log -Value "DipTick setup: $text" -Encoding UTF8
    }
}

# The runtime's version as its installer recorded it, or null when there is none.
function Get-InstalledRuntime {
    foreach ($key in 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
                     'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64') {
        $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($null -eq $item) { continue }
        $values = $item.PSObject.Properties
        if ($values['Installed'] -and $values['Major'] -and $values['Minor'] -and $values['Bld'] -and $item.Installed -eq 1) {
            return New-Object System.Version ([int] $item.Major), ([int] $item.Minor), ([int] $item.Bld)
        }
    }
    return $null
}

# One file from $url to $file with curl.exe, which Windows 10 and 11 carry; whether it came.
function Get-File([string] $url, [string] $file) {
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    & $curl --fail --silent --show-error --location --proto '=https' --connect-timeout 30 --max-time 900 --output $file $url
    return $LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $file)
}

# Microsoft's installer for the runtime, downloaded into $folder and checked; its path.
function Get-RuntimeInstaller([string] $folder) {
    $file = Join-Path $folder 'VC_redist.x64.exe'
    $pinned = Get-File $Exact.Url $file
    if (-not $pinned) {
        Say "Microsoft no longer serves runtime $($Exact.Version) at its address; fetching the newest from $Newest."
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        if (-not (Get-File $Newest $file)) {
            throw "the runtime's installer could not be downloaded from Microsoft; nothing was run."
        }
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        Remove-Item -LiteralPath $file -Force
        throw "the runtime's installer is not signed by Microsoft Corporation ($($signature.Status)); nothing was run."
    }
    if ($pinned -and (Get-FileHash -LiteralPath $file -Algorithm SHA512).Hash -ne $Exact.Sha512) {
        Remove-Item -LiteralPath $file -Force
        throw 'the runtime installer Microsoft served is not the file this script names; nothing was run.'
    }
    $version = [version] (Get-Item -LiteralPath $file).VersionInfo.FileVersion
    if ($version -lt $Needed) {
        Remove-Item -LiteralPath $file -Force
        throw "Microsoft served runtime $version, older than the $Needed DipTick needs; nothing was run."
    }
    Say "Microsoft's runtime installer $version is downloaded, signed by Microsoft Corporation$(if ($pinned) { ', and is the exact file named' })."
    return $file
}

# ---------------------------------------------------------------- what is here
$msi = Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'DipTick-*-x64.msi' |
    Sort-Object { [version] ($_.BaseName -replace '^DipTick-(.+)-x64$', '$1') } -Descending |
    Select-Object -First 1
if ($null -eq $msi) {
    Say "no DipTick-<version>-x64.msi beside this script, in $PSScriptRoot; nothing was changed."
    exit 1
}
$runtime = Get-InstalledRuntime
$needsRuntime = ($null -eq $runtime) -or ($runtime -lt $Needed)

if ($DryRun) {
    Say "found $($msi.Name)."
    if ($needsRuntime) {
        Say "this PC has no Visual C++ runtime $Needed or newer$(if ($runtime) { " (it has $runtime)" }): it would be installed first."
    }
    else {
        Say "this PC has Visual C++ runtime $runtime; nothing more is needed."
    }
    $folder = Join-Path ([IO.Path]::GetTempPath()) ('diptick-setup-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    try {
        $null = Get-RuntimeInstaller $folder
    }
    catch {
        Say $_.Exception.Message
        exit 1
    }
    finally {
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
    }
    Say 'dry run: nothing was installed.'
    exit 0
}

# ---------------------------------------------------------------- as administrator
$principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Say 'installing needs an administrator; Windows will ask.'
    $said = Join-Path ([IO.Path]::GetTempPath()) ('diptick-setup-' + [guid]::NewGuid().ToString('N') + '.txt')
    $shell = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Log', "`"$said`"")
    try {
        $run = Start-Process -FilePath $shell -ArgumentList $arguments -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    }
    catch {
        Say 'Windows did not start it as administrator; nothing was changed.'
        exit 1
    }
    if (Test-Path -LiteralPath $said) {
        Get-Content -LiteralPath $said | ForEach-Object { Write-Host $_ }
        Remove-Item -LiteralPath $said -Force
    }
    exit $run.ExitCode
}

try {
    # ------------------------------------------------------------ 1. the runtime
    if ($needsRuntime) {
        Say "this PC has no Visual C++ runtime $Needed or newer$(if ($runtime) { " (it has $runtime)" }); installing Microsoft's."
        $folder = Join-Path ([IO.Path]::GetTempPath()) ('diptick-setup-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        try {
            $installer = Get-RuntimeInstaller $folder
            $process = Start-Process -FilePath $installer -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
            switch ($process.ExitCode) {
                0 { Say 'the Visual C++ runtime is installed.' }
                1638 { Say 'a newer Visual C++ runtime was already installed.' }
                3010 { Say 'the Visual C++ runtime is installed; Windows wants a restart to finish it.' }
                default { throw "Microsoft's runtime installer ended with $($process.ExitCode); DipTick was not installed." }
            }
        }
        finally {
            Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Say "this PC has Visual C++ runtime $runtime."
    }

    # ------------------------------------------------------------ 2. DipTick
    Say "installing $($msi.Name)."
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList '/i', "`"$($msi.FullName)`"", '/passive', '/norestart' -Wait -PassThru
    switch ($process.ExitCode) {
        0 { Say 'DipTick is installed. Open a new terminal and type diptick; or find DipTick on the Start menu.' }
        3010 { Say 'DipTick is installed; Windows wants a restart to finish. Then type diptick in a new terminal.' }
        1602 { Say 'the install was cancelled; nothing of DipTick was installed.'; exit 1602 }
        default { Say "the MSI ended with $($process.ExitCode); see Windows' Application event log for why."; exit $process.ExitCode }
    }
    exit 0
}
catch {
    Say $_.Exception.Message
    exit 1
}
