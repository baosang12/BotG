param(
    [string]$SrcCfg = "D:\OneDrive\Tài liệu\cAlgo\Sources\Robots\BotG\config.runtime.json"
)

$ErrorActionPreference = 'Stop'

# 1) Find deployed BotG.algo
$searchRoots = @($env:LOCALAPPDATA, $env:APPDATA, 'C:\Program Files', 'C:\Program Files (x86)', 'C:\')
$found = @()
foreach ($r in $searchRoots) {
  try {
    if ($r -and (Test-Path -LiteralPath $r)) {
      $found += Get-ChildItem -LiteralPath $r -Filter 'BotG.algo' -Recurse -ErrorAction SilentlyContinue -Force -File
    }
  } catch {}
}
$found = $found | Sort-Object LastWriteTime -Descending
if (-not $found -or $found.Count -eq 0) {
  Write-Output 'MISSING: BotG.algo not found'
  exit 1
}
$deployed = $found[0].FullName
Write-Output ("DEPLOYED_ALGO=" + $deployed)
Write-Output ("DEPLOYED_ALGO_LASTWRITE=" + ($found[0].LastWriteTime.ToString('o')))

# 2) Backup and copy config.runtime.json
$deployDir = Split-Path -Parent $deployed
$targetCfg = Join-Path $deployDir 'config.runtime.json'
if (-not (Test-Path -LiteralPath $SrcCfg)) {
  Write-Output ("COPY=ERROR: source config not found: " + $SrcCfg)
  exit 2
}
if (Test-Path -LiteralPath $targetCfg) {
  $bak = Join-Path $deployDir ("config.runtime.json.bak." + (Get-Date -Format yyyyMMdd_HHmmss))
  try {
    Copy-Item -LiteralPath $targetCfg -Destination $bak -Force
    Write-Output ("BACKUP=" + $bak)
  } catch {
    Write-Output ("BACKUP=ERROR: " + $_.Exception.Message)
  }
} else {
  Write-Output 'BACKUP=SKIPPED'
}
try {
  Copy-Item -LiteralPath $SrcCfg -Destination $targetCfg -Force
  Write-Output 'COPY=OK'
} catch {
  Write-Output ("COPY=ERROR: " + $_.Exception.Message)
  exit 3
}

# 3) Set system env BOTG_LOG_PATH=D:\botg\logs (no restart)
try {
  $p = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" -ArgumentList '/c','setx','BOTG_LOG_PATH','D:\botg\logs','/M' -NoNewWindow -PassThru
  $p.WaitForExit()
  if ($p.ExitCode -eq 0) { Write-Output 'SET_ENV=OK' } else { Write-Output ('SET_ENV=ERROR: exit ' + $p.ExitCode) }
} catch {
  Write-Output ('SET_ENV=ERROR: ' + $_.Exception.Message)
}

try {
  $val=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment').GetValue('BOTG_LOG_PATH')
  if ($val) { Write-Output ('SET_ENV_VALUE=' + $val) } else { Write-Output 'SET_ENV_VALUE=' }
} catch {
  Write-Output ('SET_ENV_VALUE=ERROR: ' + $_.Exception.Message)
}
