# Clean-Project.ps1
# Removes all bin, obj, and publish directories across the solution to ensure a clean build state.

param(
    [Parameter(Mandatory = $false)]
    [switch]$IncludePublish = $false
)

$ErrorActionPreference = "Continue"

Write-Host "Cleaning Swarm project..." -ForegroundColor Cyan

# Find and remove bin/obj directories
$directories = Get-ChildItem -Path . -Include bin, obj -Recurse -Directory

if ($directories.Count -eq 0) {
    Write-Host "No build artifacts (bin/obj) found to clean." -ForegroundColor Green
}
else {
    foreach ($dir in $directories) {
        try {
            Write-Host "Removing: $($dir.FullName)" -ForegroundColor Gray
            Remove-Item -Path $dir.FullName -Recurse -Force
        }
        catch {
            Write-Warning "Could not remove $($dir.FullName): $($_.Exception.Message)"
        }
    }
}

# Optionally clean publish directory
if ($IncludePublish -and (Test-Path "publish")) {
    Write-Host "Removing: publish directory" -ForegroundColor Gray
    try {
        Remove-Item -Path "publish" -Recurse -Force
        Write-Host "Removed publish directory." -ForegroundColor Green
    }
    catch {
        Write-Warning "Could not remove publish directory: $($_.Exception.Message)"
    }
}

Write-Host "Done cleaning build artifacts." -ForegroundColor Green

# Usage hints
Write-Host "`nUsage:" -ForegroundColor Yellow
Write-Host "  .\Clean-Project.ps1                  # Clean bin/obj only" -ForegroundColor Gray
Write-Host "  .\Clean-Project.ps1 -IncludePublish  # Clean bin/obj and publish directories" -ForegroundColor Gray
