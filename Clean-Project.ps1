# Clean-Project.ps1
# Removes all bin, obj, and publish directories across the solution to ensure a clean build state.

param(
    [Parameter(Mandatory = $false)]
    [switch]$IncludePublish = $false,

    [Parameter(Mandatory = $false)]
    [ValidateSet("all", "win-x64", "linux-x64")]
    [string]$Platform = "all"
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
if ($IncludePublish) {
    if ($Platform -eq "all" -and (Test-Path "publish")) {
        Write-Host "Removing: entire publish directory" -ForegroundColor Gray
        try {
            Remove-Item -Path "publish" -Recurse -Force
            Write-Host "Removed publish directory." -ForegroundColor Green
        }
        catch {
            Write-Warning "Could not remove publish directory: $($_.Exception.Message)"
        }
    }
    elseif ($Platform -ne "all") {
        $PlatformDir = "publish/$Platform"
        if (Test-Path $PlatformDir) {
            Write-Host "Removing: $PlatformDir" -ForegroundColor Gray
            try {
                Remove-Item -Path $PlatformDir -Recurse -Force
                Write-Host "Removed $Platform publish directory." -ForegroundColor Green
            }
            catch {
                Write-Warning "Could not remove ${PlatformDir}: $($_.Exception.Message)"
            }
        }
        else {
            Write-Host "No publish directory for $Platform found." -ForegroundColor Yellow
        }
    }
}

Write-Host "Done cleaning build artifacts." -ForegroundColor Green

# Usage hints
Write-Host "`nUsage:" -ForegroundColor Yellow
Write-Host "  .\Clean-Project.ps1                              # Clean bin/obj only" -ForegroundColor Gray
Write-Host "  .\Clean-Project.ps1 -IncludePublish              # Clean bin/obj and all publish dirs" -ForegroundColor Gray
Write-Host "  .\Clean-Project.ps1 -IncludePublish -Platform win-x64  # Clean bin/obj + win-x64 publish" -ForegroundColor Gray
Write-Host "  .\Clean-Project.ps1 -IncludePublish -Platform linux-x64  # Clean bin/obj + linux-x64 publish" -ForegroundColor Gray
