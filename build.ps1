[CmdletBinding()]
param(
    [switch]$Portable,
    [switch]$Installer,
    [switch]$FrameworkDependent,
    [switch]$Loose,
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (-not $Portable -and -not $Installer) { $Portable = $true; $Installer = $true }

$selfContainedFlag = if ($FrameworkDependent) { 'false' } else { 'true' }
$script:appPublish = $null
$script:scratch = @()

function Publish-App {
    if ($script:appPublish) { return $script:appPublish }

    $outDir = Join-Path $env:TEMP "clipwatch-app-$([guid]::NewGuid().ToString('N'))"
    $script:scratch += $outDir

    $publishArgs = @(
        'publish', (Join-Path $root 'ClipWatch.csproj'),
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $selfContainedFlag,
        '-p:PublishSingleFile=true',

        '-p:ClipWatchPackaging=true',
        "-p:Version=$Version",
        '-o', $outDir,
        '--nologo', '-v', 'quiet'
    )

    Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $exe = Join-Path $outDir 'ClipWatch.exe'
    if (-not (Test-Path $exe)) { throw "publish produced no ClipWatch.exe." }

    $script:appPublish = $outDir
    return $outDir
}

New-Item -ItemType Directory -Force $dist | Out-Null

try {
    if ($Portable) {
        Write-Host "`nPortable" -ForegroundColor Cyan

        $appDir = Publish-App
        $target = Join-Path $dist "ClipWatch-$Version-portable.exe"
        Copy-Item (Join-Path $appDir 'ClipWatch.exe') $target -Force

        $size = [math]::Round((Get-Item $target).Length / 1MB, 1)
        $shape = if ($FrameworkDependent) { 'needs .NET 8 Desktop Runtime' } else { 'standalone' }
        Write-Host "  -> $target ($size MB, $shape)" -ForegroundColor Green
    }

    if ($Installer) {
        Write-Host "`nInstaller" -ForegroundColor Cyan

        $appDir = Publish-App

        $work = Join-Path $env:TEMP "clipwatch-installer-$([guid]::NewGuid().ToString('N'))"
        $script:scratch += $work
        $payloadDir = Join-Path $work 'app'
        New-Item -ItemType Directory -Force $payloadDir | Out-Null

        Copy-Item (Join-Path $appDir 'ClipWatch.exe') $payloadDir -Force
        Copy-Item (Join-Path $root 'installer\uninstall.ps1') $payloadDir -Force

        $zip = Join-Path $work 'payload.zip'
        Compress-Archive -Path (Join-Path $payloadDir '*') -DestinationPath $zip -CompressionLevel Optimal

        $setupOut = Join-Path $work 'setup'
        $setupArgs = @(
            'publish', (Join-Path $root 'installer\ClipWatch.Setup\ClipWatch.Setup.csproj'),
            '-c', 'Release',
            "-p:Version=$Version",
            "-p:PayloadZip=$zip",
            "-p:InstallScript=$(Join-Path $root 'installer\install.ps1')",
            "-p:UninstallScript=$(Join-Path $root 'installer\uninstall.ps1')",
            '-o', $setupOut,
            '--nologo', '-v', 'quiet'
        )

        Write-Host "  dotnet $($setupArgs -join ' ')" -ForegroundColor DarkGray
        & dotnet @setupArgs
        if ($LASTEXITCODE -ne 0) { throw "Building the installer failed with exit code $LASTEXITCODE." }

        $target = Join-Path $dist "ClipWatch-Setup-$Version.exe"
        Copy-Item (Join-Path $setupOut 'ClipWatch-Setup.exe') $target -Force

        if ($Loose) {
            $payload = Join-Path $dist "ClipWatch-$Version-installer"
            if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
            New-Item -ItemType Directory -Force $payload | Out-Null
            Copy-Item $payloadDir (Join-Path $payload 'app') -Recurse -Force
            Copy-Item (Join-Path $root 'installer\install.ps1') $payload -Force
            Set-Content (Join-Path $payload 'version.txt') $Version -Encoding ascii
            Write-Host "  -> $payload (loose payload)" -ForegroundColor Green
        }

        $size = [math]::Round((Get-Item $target).Length / 1MB, 1)
        Write-Host "  -> $target ($size MB)" -ForegroundColor Green
        Write-Host "     Double-click to install. /S installs silently, /uninstall removes." -ForegroundColor DarkGray
    }
}
finally {
    foreach ($dir in $script:scratch) {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
