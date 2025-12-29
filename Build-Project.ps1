# Build-Project.ps1
# Builds and publishes the Swarm Avalonia application for various platforms.

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64")]
    [string]$Platform = "win-x64",

    [Parameter(Mandatory = $false)]
    [switch]$Portable = $false,

    [Parameter(Mandatory = $false)]
    [switch]$All = $false
)

$ErrorActionPreference = "Stop"

function Build-Platform {
    param([string]$TargetPlatform, [bool]$IsPortable)

    Write-Host "`nBuilding Swarm for $TargetPlatform..." -ForegroundColor Cyan

    $PublishDir = "publish/$TargetPlatform"
    if (Test-Path $PublishDir) {
        Remove-Item -Path $PublishDir -Recurse -Force
    }

    # Run dotnet publish
    Write-Host "Executing: dotnet publish Swarm.Avalonia -c Release -r $TargetPlatform --self-contained -o $PublishDir" -ForegroundColor Gray
    dotnet publish Swarm.Avalonia/Swarm.Avalonia.csproj -c Release -r $TargetPlatform --self-contained true -o $PublishDir /p:PublishSingleFile=true /p:PublishTrimmed=false

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $TargetPlatform"
    }

    if ($IsPortable) {
        Write-Host "Adding portable.marker to publish directory..." -ForegroundColor Yellow
        New-Item -Path "$PublishDir/portable.marker" -ItemType File -Force | Out-Null
    }

    Write-Host "Built $TargetPlatform successfully!" -ForegroundColor Green
    return $PublishDir
}

# Build all platforms or just the specified one
if ($All) {
    Write-Host "Building Swarm for all primary platforms..." -ForegroundColor Magenta
    $Platforms = @("win-x64", "linux-x64")
    $Results = @()

    foreach ($P in $Platforms) {
        try {
            $Dir = Build-Platform -TargetPlatform $P -IsPortable $Portable
            $Results += @{ Platform = $P; Status = "Success"; Path = $Dir }
        }
        catch {
            $Results += @{ Platform = $P; Status = "Failed"; Error = $_.Exception.Message }
        }
    }

    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "Build Summary" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    foreach ($R in $Results) {
        if ($R.Status -eq "Success") {
            Write-Host "  $($R.Platform): SUCCESS -> $($R.Path)" -ForegroundColor Green
        }
        else {
            Write-Host "  $($R.Platform): FAILED - $($R.Error)" -ForegroundColor Red
        }
    }
}
else {
    Build-Platform -TargetPlatform $Platform -IsPortable $Portable
    Write-Host "`nBuild Complete!" -ForegroundColor Green
    Write-Host "Output location: $(Get-Item "publish/$Platform").FullName" -ForegroundColor White
    if ($Portable) {
        Write-Host "Portable mode enabled (portable.marker included)." -ForegroundColor Yellow
    }
}

# Usage hints
Write-Host "`nUsage:" -ForegroundColor Yellow
Write-Host "  .\Build-Project.ps1                  # Build for win-x64" -ForegroundColor Gray
Write-Host "  .\Build-Project.ps1 -Platform linux-x64  # Build for linux-x64" -ForegroundColor Gray
Write-Host "  .\Build-Project.ps1 -All             # Build for win-x64 AND linux-x64" -ForegroundColor Gray
Write-Host "  .\Build-Project.ps1 -Portable        # Build with portable.marker" -ForegroundColor Gray
