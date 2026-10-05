[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "dist",
    [switch]$SkipNpm
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path "$PSScriptRoot\.."
$FullOutputDir = Join-Path $RepoRoot $OutputDir
$StageDir = Join-Path $RepoRoot "temp-stage-$Runtime"

Write-Host "==> Packaging Garf for $Runtime ($Configuration) ..." -ForegroundColor Cyan

# 1. Clean staging & ensure output dir
if (Test-Path $StageDir) {
    Remove-Item -Recurse -Force $StageDir
}
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
New-Item -ItemType Directory -Path $FullOutputDir -Force | Out-Null

# 2. Build self-contained single-file binary
Write-Host "==> Publishing Garf.Indexer ($Runtime) ..." -ForegroundColor Cyan
dotnet publish "$RepoRoot\src\Garf.Indexer\Garf.Indexer.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$StageDir"

# Remove any unnecessary pdb or extra artifacts from stage if single-file generated
Get-ChildItem -Path $StageDir -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

# 3. Bundle ts-indexer
$TsSourceDir = Join-Path $RepoRoot "ts-indexer"
$TsStageDir = Join-Path $StageDir "ts-indexer"
Write-Host "==> Staging ts-indexer ..." -ForegroundColor Cyan

if (-not $SkipNpm) {
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        if (-not (Test-Path "$TsSourceDir\node_modules")) {
            Write-Host "==> Running npm install in $TsSourceDir ..." -ForegroundColor Cyan
            Push-Location $TsSourceDir
            try {
                npm install --omit=dev
            } finally {
                Pop-Location
            }
        }
    } else {
        Write-Warning "npm not found on system PATH. Packaging ts-indexer without checking dependencies."
    }
}

New-Item -ItemType Directory -Path $TsStageDir -Force | Out-Null
Copy-Item "$TsSourceDir\index.mjs" "$TsStageDir\" -Force
Copy-Item "$TsSourceDir\package.json" "$TsStageDir\" -Force
if (Test-Path "$TsSourceDir\package-lock.json") {
    Copy-Item "$TsSourceDir\package-lock.json" "$TsStageDir\" -Force
}
if (Test-Path "$TsSourceDir\node_modules") {
    Copy-Item -Recurse "$TsSourceDir\node_modules" "$TsStageDir\" -Force
}

# 4. Map runtime name to distribution archive name
$ArchiveName = switch ($Runtime) {
    "win-x64"   { "garf-windows-x64.zip" }
    "win-arm64" { "garf-windows-arm64.zip" }
    "linux-x64" { "garf-linux-x64.tar.gz" }
    "linux-arm64" { "garf-linux-arm64.tar.gz" }
    "osx-x64"   { "garf-macos-x64.tar.gz" }
    "osx-arm64" { "garf-macos-arm64.tar.gz" }
    default     { "garf-$Runtime.zip" }
}

$ArchivePath = Join-Path $FullOutputDir $ArchiveName
if (Test-Path $ArchivePath) {
    Remove-Item -Force $ArchivePath
}

Write-Host "==> Creating archive: $ArchivePath ..." -ForegroundColor Cyan

if ($ArchiveName.EndsWith(".zip")) {
    Compress-Archive -Path "$StageDir\*" -DestinationPath $ArchivePath -Force
} elseif ($ArchiveName.EndsWith(".tar.gz")) {
    tar -czf "$ArchivePath" -C "$StageDir" .
}

# 5. Compute SHA256 checksum
$Hash = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$ChecksumLine = "$Hash  $ArchiveName"
$ChecksumFile = Join-Path $FullOutputDir "checksums.txt"
Add-Content -Path $ChecksumFile -Value $ChecksumLine

Write-Host "==> Packaged successfully!" -ForegroundColor Green
Write-Host "    Archive:  $ArchivePath" -ForegroundColor Green
Write-Host "    SHA256:   $Hash" -ForegroundColor Green

# 6. Cleanup stage
Remove-Item -Recurse -Force $StageDir -ErrorAction SilentlyContinue
