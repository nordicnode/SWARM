# Clean-Build.ps1
# Cleans up all build artifacts for the Swarm application

$ErrorActionPreference = "Stop"

Write-Host "Cleaning Swarm build artifacts..." -ForegroundColor Cyan

# Directories to clean
$DirsToClean = @(
    ".\bin",
    ".\obj",
    ".\publish"
)

$TotalRemoved = 0

foreach ($Dir in $DirsToClean) {
    if (Test-Path $Dir) {
        Write-Host "Removing $Dir..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $Dir
        $TotalRemoved++
    }
}

# Also run dotnet clean for good measure
Write-Host "Running dotnet clean..." -ForegroundColor Yellow
dotnet clean --nologo -v q

if ($TotalRemoved -gt 0 -or $LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Clean complete!" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Nothing to clean." -ForegroundColor Gray
}
