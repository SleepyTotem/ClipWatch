[CmdletBinding()]
param(
    [switch]$Settings,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$target = $PSScriptRoot

if (-not $Quiet) {
    Write-Host ""
    Write-Host "Uninstalling ClipWatch" -ForegroundColor Cyan
    Write-Host "  from $target" -ForegroundColor DarkGray
}

Get-Process ClipWatch -ErrorAction SilentlyContinue | ForEach-Object {
    if (-not $Quiet) { Write-Host "  Closing ClipWatch..." -ForegroundColor DarkGray }
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

foreach ($root in @([Environment]::GetFolderPath('StartMenu'),
                    [Environment]::GetFolderPath('CommonStartMenu'))) {
    $shortcut = Join-Path $root 'Programs\ClipWatch.lnk'
    if (Test-Path $shortcut) {
        Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
        if (-not $Quiet) { Write-Host "  Shortcut removed." -ForegroundColor DarkGray }
    }
}

foreach ($key in @('HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClipWatch',
                   'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClipWatch')) {
    if (Test-Path $key) {
        Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue
        if (-not $Quiet) { Write-Host "  Registry entry removed." -ForegroundColor DarkGray }
    }
}

if ($Settings) {
    $config = Join-Path $env:APPDATA 'ClipWatch'
    if (Test-Path $config) {
        Remove-Item $config -Recurse -Force -ErrorAction SilentlyContinue
        if (-not $Quiet) { Write-Host "  Settings removed." -ForegroundColor DarkGray }
    }
}

$command = "timeout /t 2 /nobreak >nul & rmdir /s /q `"$target`""
Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $command `
              -WindowStyle Hidden -ErrorAction SilentlyContinue

if (-not $Quiet) {
    Write-Host ""
    Write-Host "Uninstalled." -ForegroundColor Green
    if (-not $Settings) {
        Write-Host "  Settings kept in %APPDATA%\ClipWatch (re-run with -Settings to remove them)." -ForegroundColor DarkGray
    }
    Write-Host ""
}
