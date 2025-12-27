# Build-Swarm.ps1
# Builds a single, self-contained .exe for the Swarm application

$ErrorActionPreference = "Stop"

Write-Host "Building Swarm as a self-contained executable..." -ForegroundColor Cyan

# Configuration
$Configuration = "Release"
$Runtime = "win-x64"
$OutputDir = ".\publish"

# Clean previous publish output
if (Test-Path $OutputDir) {
    Write-Host "Cleaning previous publish output..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $OutputDir
}

# Build self-contained single-file executable
Write-Host "Publishing Swarm..." -ForegroundColor Cyan

dotnet publish Swarm.csproj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutputDir

if ($LASTEXITCODE -eq 0) {
    $ExePath = Join-Path $OutputDir "Swarm.exe"
    if (Test-Path $ExePath) {
        $FileInfo = Get-Item $ExePath
        $SizeMB = [math]::Round($FileInfo.Length / 1MB, 2)
        
        Write-Host ""
        Write-Host "Build successful!" -ForegroundColor Green
        Write-Host "Output: $($FileInfo.FullName)" -ForegroundColor White
        Write-Host "Size:   $SizeMB MB" -ForegroundColor White
    }
}
else {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
