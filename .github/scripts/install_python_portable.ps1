#Requires -Version 5.1
<#
.SYNOPSIS
    Install Python 3.11 portable (embeddable zip) on Windows without requiring registry access.

.DESCRIPTION
    Downloads Python 3.11.x embeddable zip, extracts to .python directory, and adds to PATH.
    Fallback solution for self-hosted Windows runners where setup-python action fails.

.PARAMETER PythonVersion
    Python version to install (default: 3.11.9)

.PARAMETER InstallDir
    Installation directory (default: .python in repo root)

.EXAMPLE
    .\install_python_portable.ps1
    .\install_python_portable.ps1 -PythonVersion "3.11.10" -InstallDir "C:\python311"
#>

[CmdletBinding()]
param(
    [string]$PythonVersion = "3.11.9",
    [string]$InstallDir = (Join-Path $PWD ".python")
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-ColorOutput {
    param([string]$Message, [string]$Color = 'White')
    Write-Host $Message -ForegroundColor $Color
}

function Install-PythonPortable {
    Write-ColorOutput "`n=== Installing Python $PythonVersion Portable ===" -Color Cyan
    
    # Parse version
    $versionParts = $PythonVersion.Split('.')
    $majorMinor = "$($versionParts[0]).$($versionParts[1])"
    $embeddableVersion = $PythonVersion -replace '\.', ''
    
    # URLs
    $downloadUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
    $zipPath = Join-Path $env:TEMP "python-$PythonVersion-embed.zip"
    
    Write-ColorOutput "Download URL: $downloadUrl" -Color Gray
    Write-ColorOutput "Install Dir: $InstallDir" -Color Gray
    
    # Create install directory
    if (Test-Path $InstallDir) {
        Write-ColorOutput "Cleaning existing installation..." -Color Yellow
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    
    # Download
    Write-ColorOutput "Downloading Python embeddable zip..." -Color Yellow
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
        Write-ColorOutput "[✓] Downloaded: $zipPath" -Color Green
    }
    catch {
        Write-ColorOutput "[✗] Download failed: $_" -Color Red
        throw
    }
    
    # Extract
    Write-ColorOutput "Extracting..." -Color Yellow
    try {
        Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
        Write-ColorOutput "[✓] Extracted to: $InstallDir" -Color Green
    }
    catch {
        Write-ColorOutput "[✗] Extraction failed: $_" -Color Red
        throw
    }
    finally {
        Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue
    }
    
    # Verify python.exe exists
    $pythonExe = Join-Path $InstallDir "python.exe"
    if (-not (Test-Path $pythonExe)) {
        Write-ColorOutput "[✗] python.exe not found after extraction!" -Color Red
        throw "Python executable not found at $pythonExe"
    }
    
    # Configure _pth file to enable site-packages
    Write-ColorOutput "Configuring site-packages..." -Color Yellow
    $pthFile = Get-ChildItem -Path $InstallDir -Filter "python$embeddableVersion._pth" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pthFile) {
        $pthContent = Get-Content $pthFile.FullName
        $newContent = $pthContent -replace '^#import site', 'import site'
        $newContent | Set-Content $pthFile.FullName -Encoding UTF8
        Write-ColorOutput "[✓] Enabled site-packages in $($pthFile.Name)" -Green
    }
    
    # Download and install pip
    Write-ColorOutput "Installing pip..." -Color Yellow
    $getPipUrl = "https://bootstrap.pypa.io/get-pip.py"
    $getPipPath = Join-Path $env:TEMP "get-pip.py"
    try {
        Invoke-WebRequest -Uri $getPipUrl -OutFile $getPipPath -UseBasicParsing
        & $pythonExe $getPipPath --no-warn-script-location 2>&1 | Write-Host
        Write-ColorOutput "[✓] pip installed" -Color Green
    }
    catch {
        Write-ColorOutput "[⚠] pip installation failed (non-critical): $_" -Color Yellow
    }
    finally {
        Remove-Item -Path $getPipPath -Force -ErrorAction SilentlyContinue
    }
    
    # Add to PATH (session-level)
    Write-ColorOutput "Adding to PATH..." -Color Yellow
    $env:Path = "$InstallDir;$env:Path"
    Write-ColorOutput "[✓] Added to PATH (current session)" -Color Green
    
    # Verify installation
    Write-ColorOutput "`nVerifying installation..." -Color Cyan
    try {
        $version = & $pythonExe --version 2>&1
        Write-ColorOutput "[✓] Python version: $version" -Color Green
        
        $location = & $pythonExe -c "import sys; print(sys.executable)" 2>&1
        Write-ColorOutput "[✓] Location: $location" -Color Green
        
        # Test pip
        $pipVersion = & $pythonExe -m pip --version 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-ColorOutput "[✓] pip: $pipVersion" -Color Green
        } else {
            Write-ColorOutput "[⚠] pip not available (install with: python -m ensurepip)" -Color Yellow
        }
    }
    catch {
        Write-ColorOutput "[✗] Verification failed: $_" -Color Red
        throw
    }
    
    Write-ColorOutput "`n=== Installation Complete ===" -Color Cyan
    Write-ColorOutput "Python executable: $pythonExe" -Color White
    Write-ColorOutput "To use: Add '$InstallDir' to PATH or call '$pythonExe' directly" -Color White
    
    return @{
        PythonExe = $pythonExe
        InstallDir = $InstallDir
        Version = $version
    }
}

# Main execution
try {
    $result = Install-PythonPortable
    
    # Output for GitHub Actions
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host "::set-output name=python-path::$($result.InstallDir)"
        Write-Host "::set-output name=python-exe::$($result.PythonExe)"
        Write-Host "::add-path::$($result.InstallDir)"
    }
    
    exit 0
}
catch {
    Write-ColorOutput "`n[FATAL] Installation failed: $_" -Color Red
    Write-ColorOutput $_.ScriptStackTrace -Color Red
    exit 1
}
