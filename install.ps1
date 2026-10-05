[CmdletBinding()]
param(
    [string]$InstallDir = "$env:USERPROFILE\.garf",
    [string]$Version = "latest",
    [string]$Repo = "namkhanh307/Garf",
    [string]$LocalArchive = "",
    [switch]$FromSource
)

$ErrorActionPreference = "Stop"

Write-Host @"
=====================================================
            Installing Garf Code Indexer
=====================================================
"@ -ForegroundColor Cyan

$BinDir = Join-Path $InstallDir "bin"
$TempZip = Join-Path $env:TEMP "garf-install-$([Guid]::NewGuid().ToString('N')).zip"
$TempExtract = Join-Path $env:TEMP "garf-extract-$([Guid]::NewGuid().ToString('N'))"

try {
    # 1. Obtain installation bundle
    if ($LocalArchive -and (Test-Path $LocalArchive)) {
        Write-Host "==> Using local archive: $LocalArchive" -ForegroundColor Yellow
        Copy-Item -Path $LocalArchive -Destination $TempZip -Force
    }
    elseif ($FromSource) {
        Write-Host "==> Building from current source tree ..." -ForegroundColor Yellow
        $ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Get-Location }
        $PackageScript = Join-Path $ScriptDir "scripts\package.ps1"
        if (-not (Test-Path $PackageScript)) {
            throw "scripts\package.ps1 not found in $ScriptDir"
        }
        $DistDir = Join-Path $ScriptDir "dist"
        & powershell -ExecutionPolicy Bypass -File $PackageScript -OutputDir "dist"
        $BuiltZip = Join-Path $DistDir "garf-windows-x64.zip"
        if (-not (Test-Path $BuiltZip)) {
            throw "Failed to build distribution archive at $BuiltZip"
        }
        Copy-Item -Path $BuiltZip -Destination $TempZip -Force
    }
    else {
        # Fetch from GitHub Releases
        $Arch = if ([System.Environment]::Is64BitOperatingSystem) { "x64" } else { "x86" }
        if ($Arch -ne "x64") {
            throw "Garf currently requires a 64-bit operating system."
        }
        $AssetFileName = "garf-windows-x64.zip"

        # Attempt direct release download first (fastest, avoids API rate limits)
        $DirectDownloadUrl = if ($Version -eq "latest") {
            "https://github.com/$Repo/releases/latest/download/$AssetFileName"
        } else {
            "https://github.com/$Repo/releases/download/$Version/$AssetFileName"
        }

        Write-Host "==> Checking release asset at $DirectDownloadUrl ..." -ForegroundColor Cyan
        try {
            Invoke-WebRequest -Uri $DirectDownloadUrl -OutFile $TempZip -UseBasicParsing -TimeoutSec 15
            Write-Host "==> Successfully downloaded release asset from GitHub." -ForegroundColor Green
        }
        catch {
            Write-Warning "Direct release asset download unavailable ($($_.Exception.Message))."
        }

        if (-not (Test-Path $TempZip)) {
            # Try GitHub API release query
            $ReleaseApiUrl = if ($Version -eq "latest") {
                "https://api.github.com/repos/$Repo/releases/latest"
            } else {
                "https://api.github.com/repos/$Repo/releases/tags/$Version"
            }

            try {
                $headers = @{ "User-Agent" = "garf-installer" }
                $releaseJson = Invoke-RestMethod -Uri $ReleaseApiUrl -Headers $headers -Method Get -TimeoutSec 15
                $asset = $releaseJson.assets | Where-Object { $_.name -eq $AssetFileName }
                if ($asset -and $asset.browser_download_url) {
                    Write-Host "==> Downloading $AssetFileName from release ($($releaseJson.tag_name)) ..." -ForegroundColor Cyan
                    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $TempZip -UseBasicParsing
                }
            }
            catch {
                Write-Warning "GitHub API query also failed ($($_.Exception.Message))."
            }
        }

        # Fallback 1: Local repository if run from repo folder
        if (-not (Test-Path $TempZip)) {
            $CandidateLocalZip = Join-Path (Get-Location) "dist\$AssetFileName"
            if (Test-Path $CandidateLocalZip) {
                Write-Host "==> Found local distribution archive: $CandidateLocalZip" -ForegroundColor Yellow
                Copy-Item -Path $CandidateLocalZip -Destination $TempZip -Force
            }
            elseif ((Test-Path (Join-Path (Get-Location) "src\Garf.Indexer\Garf.Indexer.csproj")) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
                Write-Host "==> Compiling from local repository clone ..." -ForegroundColor Yellow
                $PackageScript = Join-Path (Get-Location) "scripts\package.ps1"
                if (Test-Path $PackageScript) {
                    & powershell -ExecutionPolicy Bypass -File $PackageScript -OutputDir "dist"
                    $BuiltZip = Join-Path (Get-Location) "dist\$AssetFileName"
                    if (Test-Path $BuiltZip) {
                        Copy-Item -Path $BuiltZip -Destination $TempZip -Force
                    }
                }
            }
        }

        # Fallback 2: Download source zip from GitHub and compile with local dotnet SDK
        if (-not (Test-Path $TempZip) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            Write-Host "==> Release asset not yet published on GitHub. Downloading source archive to compile with local .NET SDK fallback ..." -ForegroundColor Yellow
            $SourceZipUrl = "https://github.com/$Repo/archive/refs/heads/main.zip"
            $TempSourceZip = Join-Path $env:TEMP "garf-source-$([Guid]::NewGuid().ToString('N')).zip"
            $TempSourceDir = Join-Path $env:TEMP "garf-source-extract-$([Guid]::NewGuid().ToString('N'))"
            try {
                Invoke-WebRequest -Uri $SourceZipUrl -OutFile $TempSourceZip -UseBasicParsing
                Expand-Archive -Path $TempSourceZip -DestinationPath $TempSourceDir -Force
                $ExtractedRoot = Get-ChildItem -Path $TempSourceDir -Directory | Select-Object -First 1
                if ($ExtractedRoot) {
                    $TempPackageScript = Join-Path $ExtractedRoot.FullName "scripts\package.ps1"
                    if (Test-Path $TempPackageScript) {
                        & powershell -ExecutionPolicy Bypass -File $TempPackageScript -OutputDir "dist"
                        $BuiltZip = Join-Path $ExtractedRoot.FullName "dist\$AssetFileName"
                        if (Test-Path $BuiltZip) {
                            Copy-Item -Path $BuiltZip -Destination $TempZip -Force
                        }
                    }
                }
            }
            catch {
                Write-Warning "Source compilation fallback failed: $($_.Exception.Message)"
            }
            finally {
                if (Test-Path $TempSourceZip) { Remove-Item -Force $TempSourceZip -ErrorAction SilentlyContinue }
                if (Test-Path $TempSourceDir) { Remove-Item -Recurse -Force $TempSourceDir -ErrorAction SilentlyContinue }
            }
        }

        if (-not (Test-Path $TempZip)) {
            throw "Unable to download prebuilt binary '$AssetFileName' from $Repo (release may still be building on GitHub Actions), and no local fallback could be used. Check: https://github.com/$Repo/releases"
        }
    }

    # 2. Extract into destination
    Write-Host "==> Extracting into $BinDir ..." -ForegroundColor Cyan
    if (-not (Test-Path $BinDir)) {
        New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
    }

    Expand-Archive -Path $TempZip -DestinationPath $TempExtract -Force

    # Copy files into $BinDir
    Copy-Item -Path "$TempExtract\*" -Destination $BinDir -Recurse -Force

    # 3. Environment PATH Injection
    Write-Host "==> Configuring User PATH environment variable ..." -ForegroundColor Cyan
    $UserPath = [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
    $PathParts = if ($UserPath) { $UserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) } else { @() }
    
    $AlreadyInPath = $false
    foreach ($p in $PathParts) {
        if ($p.TrimEnd('\') -ieq $BinDir.TrimEnd('\')) {
            $AlreadyInPath = $true
            break
        }
    }

    if (-not $AlreadyInPath) {
        $NewUserPath = if ($UserPath) { "$UserPath;$BinDir" } else { $BinDir }
        [Environment]::SetEnvironmentVariable("Path", $NewUserPath, [EnvironmentVariableTarget]::User)
        Write-Host "==> Successfully added $BinDir to User PATH." -ForegroundColor Green
    } else {
        Write-Host "==> $BinDir is already in User PATH." -ForegroundColor DarkGray
    }

    # Also update current PowerShell process PATH so garf is immediately usable
    if ($env:PATH -split ';' -notcontains $BinDir) {
        $env:PATH = "$BinDir;$env:PATH"
    }

    # 4. Verify installation
    $InstalledExe = Join-Path $BinDir "garf.exe"
    if (-not (Test-Path $InstalledExe)) {
        throw "Installation failed: $InstalledExe was not created."
    }

    $VerOutput = & "$InstalledExe" --version 2>&1
    Write-Host ""
    Write-Host "=====================================================" -ForegroundColor Green
    Write-Host "      Installed Successfully: $VerOutput" -ForegroundColor Green
    Write-Host "=====================================================" -ForegroundColor Green
    Write-Host "Executable location: $InstalledExe"
    Write-Host "Installed components:"
    Write-Host "  - garf CLI (self-contained, no .NET SDK required)"
    Write-Host "  - ts-indexer (AST indexing for TS/TSX/JSX)"
    Write-Host ""
    Write-Host "Quick start:"
    Write-Host "  garf index <path-to-repo>"
    Write-Host "  garf query <symbol-name>"
    Write-Host "  garf mcp"
    Write-Host ""
    Write-Host "To configure MCP in Claude/Codex/Antigravity:"
    Write-Host @"
{
  "mcpServers": {
    "garf": {
      "command": "$($InstalledExe.Replace('\', '\\'))",
      "args": ["mcp"]
    }
  }
}
"@ -ForegroundColor DarkCyan
    Write-Host "Note: Restart your terminal if opened prior to installation to refresh PATH." -ForegroundColor Yellow
}
finally {
    if (Test-Path $TempZip) { Remove-Item -Force $TempZip -ErrorAction SilentlyContinue }
    if (Test-Path $TempExtract) { Remove-Item -Recurse -Force $TempExtract -ErrorAction SilentlyContinue }
}
