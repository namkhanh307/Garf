[CmdletBinding()]
param(
    [string]$InstallDir = "$env:USERPROFILE\.garf"
)

$ErrorActionPreference = "Stop"

Write-Host "==> Uninstalling Garf from $InstallDir ..." -ForegroundColor Cyan

$BinDir = Join-Path $InstallDir "bin"

# 1. Remove from User PATH
$UserPath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
if ($UserPath) {
    $PathParts = $UserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)
    $NewParts = @()
    foreach ($p in $PathParts) {
        if ($p.TrimEnd('\') -ine $BinDir.TrimEnd('\')) {
            $NewParts += $p
        }
    }
    $NewUserPath = $NewParts -join ';'
    [Environment]::SetEnvironmentVariable("Path", $NewUserPath, [EnvironmentVariableTarget]::User)
    Write-Host "==> Removed $BinDir from User PATH." -ForegroundColor Green
}

# 2. Remove directory
if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "==> Deleted $InstallDir." -ForegroundColor Green
}

Write-Host "==> Garf has been completely uninstalled." -ForegroundColor Green
