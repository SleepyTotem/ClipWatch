[CmdletBinding()]
param(
    [switch]$AllUsers,
    [switch]$NoShortcut,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$source = Join-Path $here 'app'

if (-not (Test-Path $source)) {
    throw "No 'app' folder next to this script. Run build.ps1 -Installer first, then run install.ps1 from the folder it produced."
}

$version = if (Test-Path (Join-Path $here 'version.txt')) {
    (Get-Content (Join-Path $here 'version.txt') -Raw).Trim()
} else { '1.0.0' }

if ($AllUsers) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal $identity
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "-AllUsers needs an elevated PowerShell. Re-run as administrator, or drop the switch to install just for you."
    }

    $target = Join-Path $env:ProgramFiles 'ClipWatch'
    $shortcutRoot = [Environment]::GetFolderPath('CommonStartMenu')
    $registryRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClipWatch'
} else {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\ClipWatch'
    $shortcutRoot = [Environment]::GetFolderPath('StartMenu')
    $registryRoot = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClipWatch'
}

$exe = Join-Path $target 'ClipWatch.exe'

if (-not $Quiet) {
    Write-Host ""
    Write-Host "Installing ClipWatch $version" -ForegroundColor Cyan
    Write-Host "  to $target" -ForegroundColor DarkGray
}

Get-Process ClipWatch -ErrorAction SilentlyContinue | ForEach-Object {
    if (-not $Quiet) { Write-Host "  Closing the running copy..." -ForegroundColor DarkGray }
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

New-Item -ItemType Directory -Force $target | Out-Null

Get-ChildItem $target -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Copy-Item (Join-Path $source '*') $target -Recurse -Force

if (-not (Test-Path $exe)) { throw "ClipWatch.exe is missing from the payload  -  the build looks incomplete." }

if (-not $NoShortcut) {
    $programs = Join-Path $shortcutRoot 'Programs'
    New-Item -ItemType Directory -Force $programs | Out-Null

    $shortcut = Join-Path $programs 'ClipWatch.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $target
    $link.IconLocation = "$exe,0"
    $link.Description = 'Automatic game clip capture and trimming for OBS.'
    $link.Save()

    [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

    if (-not $Quiet) { Write-Host "  Start Menu shortcut created." -ForegroundColor DarkGray }
}

$uninstallScript = Join-Path $target 'uninstall.ps1'
$size = [math]::Round(((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB)

New-Item -Path $registryRoot -Force | Out-Null
Set-ItemProperty $registryRoot 'DisplayName'     'ClipWatch'
Set-ItemProperty $registryRoot 'DisplayVersion'  $version
Set-ItemProperty $registryRoot 'Publisher'       'ClipWatch'
Set-ItemProperty $registryRoot 'DisplayIcon'     $exe
Set-ItemProperty $registryRoot 'InstallLocation' $target
Set-ItemProperty $registryRoot 'NoModify'        1 -Type DWord
Set-ItemProperty $registryRoot 'NoRepair'        1 -Type DWord
Set-ItemProperty $registryRoot 'EstimatedSize'   $size -Type DWord
Set-ItemProperty $registryRoot 'UninstallString' `
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
Set-ItemProperty $registryRoot 'QuietUninstallString' `
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`" -Quiet"

if (-not $Quiet) {
    Write-Host "  Registered in Add/Remove Programs." -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Installed." -ForegroundColor Green
    Write-Host "  Launch:    Start Menu -> ClipWatch"
    Write-Host "  Uninstall: Settings -> Apps, or ClipWatch-Setup.exe /uninstall"
    Write-Host ""
}
