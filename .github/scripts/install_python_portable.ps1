param([string]$Version = "3.11.9")
$ErrorActionPreference='Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Join-Path $env:RUNNER_TEMP "py-$Version"
New-Item -ItemType Directory -Force -Path $root | Out-Null
$zipName = "python-$Version-embed-amd64.zip"
$zipUrl  = "https://www.python.org/ftp/python/$Version/$zipName"
$zipPath = Join-Path $root $zipName

Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
Expand-Archive -Path $zipPath -DestinationPath $root -Force

# Bật 'import site' trong python311._pth
$maj,$min = $Version.Split('.')[0..1]
$pthFile = Join-Path $root ("python{0}{1}._pth" -f $maj,$min)   # vd: python311._pth
if (!(Test-Path $pthFile)) { throw "Missing $pthFile" }
(Get-Content $pthFile) -replace '^\s*#\s*import site','import site' | Set-Content $pthFile -Encoding ascii

# Thêm vào PATH cho job
Add-Content -Path $env:GITHUB_PATH -Value $root

& (Join-Path $root 'python.exe') --version
