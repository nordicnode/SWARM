# Build-Project.ps1
# Builds and publishes the Swarm Avalonia application for various platforms.

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64")]
    [string]$Platform = "win-x64",

    [Parameter(Mandatory = $false)]
    [switch]$Portable = $false,

    [Parameter(Mandatory = $false)]
    [switch]$All = $false,

    [Parameter(Mandatory = $false)]
    [string]$CertificatePath = "",

    [Parameter(Mandatory = $false)]
    [string]$CertificatePassword = "",

    [Parameter(Mandatory = $false)]
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Sign-Executable {
    param([string]$ExePath, [string]$CertPath, [string]$CertPassword, [string]$Timestamp)

    if (-not (Test-Path $ExePath)) {
        Write-Host "  Executable not found: $ExePath" -ForegroundColor Yellow
        return $false
    }

    Write-Host "  Signing: $ExePath" -ForegroundColor Cyan

    # Try signtool from Windows SDK
    $SignTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $SignTool) {
        # Try common Windows SDK paths
        $SdkPaths = @(
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe",
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.18362.0\x64\signtool.exe"
        )
        foreach ($Path in $SdkPaths) {
            if (Test-Path $Path) {
                $SignTool = $Path
                break
            }
        }
    }

    if (-not $SignTool) {
        Write-Host "  WARNING: signtool.exe not found. Install Windows SDK to enable code signing." -ForegroundColor Yellow
        return $false
    }

    try {
        $SignArgs = @(
            "sign",
            "/f", $CertPath,
            "/p", $CertPassword,
            "/fd", "SHA256",
            "/tr", $Timestamp,
            "/td", "SHA256",
            "/d", "SWARM - Secure P2P File Sync",
            $ExePath
        )

        & $SignTool @SignArgs 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Signed successfully!" -ForegroundColor Green
            return $true
        }
        else {
            Write-Host "  WARNING: Signing failed with exit code $LASTEXITCODE" -ForegroundColor Yellow
            return $false
        }
    }
    catch {
        Write-Host "  WARNING: Signing failed: $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

function Build-Platform {
    param([string]$TargetPlatform, [bool]$IsPortable, [string]$CertPath, [string]$CertPassword, [string]$Timestamp)

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

    # Code signing for Windows platforms
    if ($TargetPlatform -like "win-*" -and $CertPath -and (Test-Path $CertPath)) {
        Write-Host "`nCode Signing..." -ForegroundColor Magenta
        $ExeName = if ($TargetPlatform -like "win-*") { "Swarm.exe" } else { "Swarm" }
        $ExePath = Join-Path $PublishDir $ExeName
        $Signed = Sign-Executable -ExePath $ExePath -CertPath $CertPath -CertPassword $CertPassword -Timestamp $Timestamp
        if (-not $Signed) {
            Write-Host "  Continuing without signature..." -ForegroundColor Yellow
        }
    }
    elseif ($TargetPlatform -like "win-*" -and -not $CertPath) {
        Write-Host "`nWARNING: No certificate provided. Windows SmartScreen will flag unsigned executables." -ForegroundColor Yellow
        Write-Host "  Use -CertificatePath and -CertificatePassword to sign the build." -ForegroundColor Yellow
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
            $Dir = Build-Platform -TargetPlatform $P -IsPortable $Portable -CertPath $CertificatePath -CertPassword $CertificatePassword -Timestamp $TimestampServer
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
    Build-Platform -TargetPlatform $Platform -IsPortable $Portable -CertPath $CertificatePath -CertPassword $CertificatePassword -Timestamp $TimestampServer
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
Write-Host ""
Write-Host "Code Signing (Windows):" -ForegroundColor Yellow
Write-Host "  .\Build-Project.ps1 -CertificatePath 'cert.pfx' -CertificatePassword 'pass'" -ForegroundColor Gray
Write-Host "  # Requires: Windows SDK with signtool.exe" -ForegroundColor DarkGray
Write-Host "  # Obtain a code signing certificate from DigiCert, Sectigo, or similar CA" -ForegroundColor DarkGray
