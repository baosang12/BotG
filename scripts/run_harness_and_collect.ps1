#requires -Version 5.1
param(
    [int]$DurationSeconds = 60,
    [switch]$Simulate,
    [switch]$ForceRun,
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Warning $msg }
function Write-Err($msg) { Write-Host "[ERROR] $msg" -ForegroundColor Red }

function Get-RepoRoot {
    # Prefer PSScriptRoot (set when script is dot-sourced or executed), fallback to MyInvocation
    if ($PSBoundParameters.ContainsKey('Help')) { }
    try {
        if ($PSScriptRoot) { $scriptDir = $PSScriptRoot } else { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
    } catch { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
    return (Split-Path -Parent $scriptDir) # scripts/ -> repo root
}

function Show-Usage {
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  ./scripts/run_harness_and_collect.ps1 [-DurationSeconds N] [-Simulate] [-ForceRun]" -ForegroundColor Yellow
    Write-Host "Notes:" -ForegroundColor Yellow
    Write-Host "  - Attempts to checkout 'botg/telemetry-instrumentation' if exists and workspace is clean." -ForegroundColor Yellow
    Write-Host "  - On failure, prints ZIP path and JSON summary; exits non-zero." -ForegroundColor Yellow
}

function Get-GitMeta($repoRoot) {
    $branch = 'MISSING'
    $commit = 'MISSING'
    try {
        Push-Location $repoRoot
        if ((git rev-parse --is-inside-work-tree) 2>$null) {
            $branch = (git rev-parse --abbrev-ref HEAD).Trim()
            $commit = (git rev-parse HEAD).Trim()
        }
    } catch { }
    finally { Pop-Location }
    [pscustomobject]@{ Branch = $branch; Commit = $commit }
}

function Is-WorkspaceClean($repoRoot) {
    Push-Location $repoRoot
    try {
        $status = git status --porcelain 2>$null
        return [string]::IsNullOrWhiteSpace($status)
    } catch {
        return $true # if git missing, treat as clean to avoid blocking
    } finally { Pop-Location }
}

function Ensure-Branch($repoRoot, $targetBranch) {
    if ($ForceRun) { Write-Info "-ForceRun supplied; skipping branch checkout logic."; return $true }
    Push-Location $repoRoot
    try {
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Warn "git not found; skipping branch enforcement."; return $true }
        $inside = git rev-parse --is-inside-work-tree 2>$null
        if (-not $inside) { return $true }
        $current = (git rev-parse --abbrev-ref HEAD).Trim()
        $existsLocal = git show-ref --verify --quiet "refs/heads/$targetBranch"; $existsLocal = ($LASTEXITCODE -eq 0)
        $existsRemote = git show-ref --verify --quiet "refs/remotes/origin/$targetBranch"; $existsRemote = ($LASTEXITCODE -eq 0)
        if ($current -eq $targetBranch) { Write-Info "Already on branch $targetBranch"; return $true }
        if (-not ($existsLocal -or $existsRemote)) { Write-Info "Branch $targetBranch not found; continuing on $current."; return $true }
        if (-not (Is-WorkspaceClean $repoRoot)) { Write-Warn "Workspace is dirty; will not checkout $targetBranch. Abort."; return $false }
        if ($existsRemote -and -not $existsLocal) { git fetch origin $targetBranch 2>$null | Out-Null }
        git checkout $targetBranch
        if ($LASTEXITCODE -ne 0) { Write-Err "Failed to checkout $targetBranch"; return $false }
        Write-Info "Checked out $targetBranch"
        return $true
    } finally { Pop-Location }
}

function New-ArtifactPaths($repoRoot) {
    $ts = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $dir = Join-Path $repoRoot "artifacts\telemetry_run_$ts"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $zip = Join-Path $repoRoot "artifacts\telemetry_run_$ts.zip"
    [pscustomobject]@{ Timestamp=$ts; Dir=$dir; Zip=$zip; BuildLog=(Join-Path $dir 'build.log'); Summary=(Join-Path $dir 'summary.json') }
}

function Restore-And-Build($repoRoot, $buildLog) {
    Push-Location $repoRoot
    try {
        $sln = Join-Path $repoRoot 'BotG.sln'
        if (Test-Path $sln) {
            Write-Info "Running: dotnet restore '$sln'"
            & dotnet restore "$sln" 2>&1 | Tee-Object -FilePath $buildLog
            if ($LASTEXITCODE -ne 0) { Write-Err "dotnet restore failed"; return $false }

            Write-Info "Running: dotnet build '$sln' -c Debug"
            & dotnet build "$sln" -c Debug /property:GenerateFullPaths=true /consoleLoggerParameters:NoSummary 2>&1 | Tee-Object -FilePath $buildLog -Append
            if ($LASTEXITCODE -ne 0) { Write-Err "dotnet build failed"; return $false }
        } else {
            Write-Info "Running: dotnet restore '$repoRoot'"
            & dotnet restore "$repoRoot" 2>&1 | Tee-Object -FilePath $buildLog
            if ($LASTEXITCODE -ne 0) { Write-Err "dotnet restore failed"; return $false }

            Write-Info "Running: dotnet build '$repoRoot' -c Debug"
            & dotnet build "$repoRoot" -c Debug /property:GenerateFullPaths=true /consoleLoggerParameters:NoSummary 2>&1 | Tee-Object -FilePath $buildLog -Append
            if ($LASTEXITCODE -ne 0) { Write-Err "dotnet build failed"; return $false }
        }
        return $true
    } finally { Pop-Location }
}

function Find-HarnessProject($repoRoot) {
    $found = Get-ChildItem -Path $repoRoot -Filter 'Harness.csproj' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    $candidate = Join-Path $repoRoot 'Harness\Harness.csproj'
    if (Test-Path $candidate) { return $candidate }
    return $null
}

function Find-ExecutableCsproj($repoRoot) {
    $csprojs = Get-ChildItem -Path $repoRoot -Filter '*.csproj' -Recurse -ErrorAction SilentlyContinue
    foreach ($p in $csprojs) {
        try {
            [xml]$xml = Get-Content -Raw -Path $p.FullName
            $outType = $xml.Project.PropertyGroup.OutputType | Select-Object -First 1
            if ($outType -and $outType -match 'Exe') { return $p.FullName }
        } catch { }
    }
    return $null
}

function Start-Run($projectPath, $durationSec, $envVars) {
    Write-Info "Starting run: dotnet run --project `"$projectPath`""
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'dotnet'
        $psi.Arguments = "run --project `"$projectPath`" -- $durationSec"
    $psi.WorkingDirectory = (Split-Path -Parent $projectPath)
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    # Must be false to set environment variables
    $psi.UseShellExecute = $false
    if ($envVars) {
        foreach ($k in $envVars.Keys) {
            try { $psi.Environment[$k] = [string]$envVars[$k] } catch { }
        }
    }
    $proc = [System.Diagnostics.Process]::Start($psi)
        Start-Sleep -Seconds $durationSec
        Write-Info "Stopping run (PID=$($proc.Id)) for $durationSec sec"
    try { Stop-Process -Id $proc.Id -ErrorAction Stop } catch { Write-Warn "Graceful stop failed; trying -Force"; try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { Write-Warn "Could not stop process; continuing" } }
}

function Ensure-TelemetryDir($repoRoot) {
    $candidates = @()
    $configPath = Join-Path $repoRoot 'config.runtime.json'
    if (Test-Path $configPath) {
        try {
            $cfg = Get-Content -Raw -Path $configPath | ConvertFrom-Json
            $tele = $cfg.Telemetry
            if ($tele) {
                foreach ($k in 'LogDir','LogDirectory','OutputDir','OutputDirectory','Dir') {
                    if ($tele.$k) { $candidates += [string]$tele.$k }
                }
            }
        } catch { }
    }
    # Prefer repo-local logs, then C:\botg\logs
    $candidates += @((Join-Path $repoRoot 'logs'), "C:\\botg\\logs")
    foreach ($p in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($p)) {
            try {
                if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
                if (Test-Path $p) { return $p }
            } catch { continue }
        }
    }
    # Last resort
    return (Join-Path $repoRoot 'logs')
}

function Copy-IfExists($src, $dstDir) {
    if (-not $src) { return }
    if (-not (Test-Path $src)) { return }
    try {
        $leaf = Split-Path -Path $src -Leaf
        $dest = Join-Path -Path $dstDir -ChildPath $leaf
        $srcFull = (Get-Item $src).FullName
        $destFull = $null
        try { $destFull = (Get-Item -LiteralPath $dest -ErrorAction Stop).FullName } catch { $destFull = $dest }
        if ($srcFull -eq $destFull) { return }
        Copy-Item -Path $src -Destination $dstDir -Force
    } catch {
        Write-Warn ("Copy failed for {0}: {1}" -f $src, $_)
    }
}

function Count-Lines($path) {
    if (-not (Test-Path $path)) { return 'MISSING' }
    try { return (Get-Content -Path $path | Measure-Object -Line).Lines } catch { return 'MISSING' }
}

function Write-Summary($summaryPath, $meta, $buildOk, $artifacts, $counts, $timestamp) {
    $buildStatus = if ($buildOk) { 'SUCCESS' } else { 'FAIL' }
    $ordersCsv = if ($artifacts.orders) { $artifacts.orders } else { 'MISSING' }
    $riskCsv = if ($artifacts.risk) { $artifacts.risk } else { 'MISSING' }
    $telemetryCsv = if ($artifacts.telemetry) { $artifacts.telemetry } else { 'MISSING' }
    $datafetcherLog = if ($artifacts.datafetcher) { $artifacts.datafetcher } else { 'MISSING' }
    $zipPath = if ($artifacts.zip) { $artifacts.zip } else { 'MISSING' }

    $obj = [ordered]@{
        branch = $meta.Branch
        commit = $meta.Commit
        build_status = $buildStatus
        build_log = $artifacts.build_log
        artifacts = [ordered]@{
            orders_csv = $ordersCsv
            risk_snapshots_csv = $riskCsv
            telemetry_csv = $telemetryCsv
            datafetcher_log = $datafetcherLog
            zip = $zipPath
        }
        counts = [ordered]@{
            orders_rows = $counts.orders
            risk_snapshots_rows = $counts.risk
            telemetry_rows = $counts.telemetry
        }
        timestamp = $timestamp
    }
    $json = $obj | ConvertTo-Json -Depth 6
    Set-Content -Path $summaryPath -Value $json -Encoding UTF8
    $compact = ($json -replace "\r?\n", '')
    return $compact
}

try {
    if ($Help) { Show-Usage; exit 0 }
    $repoRoot = Get-RepoRoot
    Write-Info "Repo root: $repoRoot"

    $art = New-ArtifactPaths $repoRoot
    $tsIso = (Get-Date).ToString('s')

    $targetBranch = 'botg/telemetry-instrumentation'
    if (-not (Ensure-Branch $repoRoot $targetBranch)) { exit 2 }

    $buildOk = Restore-And-Build -repoRoot $repoRoot -buildLog $art.BuildLog
    $meta = Get-GitMeta $repoRoot
    if (-not $buildOk) {
        $teleDir = Ensure-TelemetryDir $repoRoot
        $orders = if ($teleDir) { Join-Path $teleDir 'orders.csv' }
        $risk = if ($teleDir) { Join-Path $teleDir 'risk_snapshots.csv' }
        $telemetry = if ($teleDir) { Join-Path $teleDir 'telemetry.csv' }
        $datafetcher = if ($teleDir) { Join-Path $teleDir 'datafetcher.log' }
        foreach ($f in @($orders,$risk,$telemetry,$datafetcher)) { Copy-IfExists $f $art.Dir }
        $counts = [pscustomobject]@{ orders='MISSING'; risk='MISSING'; telemetry='MISSING' }
        $zipPath = $art.Zip
        if (Test-Path $art.Dir) { Compress-Archive -Path (Join-Path $art.Dir '*') -DestinationPath $zipPath -Force }
        $summaryCompact = Write-Summary -summaryPath $art.Summary -meta $meta -buildOk:$false -artifacts @{ orders=$orders; risk=$risk; telemetry=$telemetry; datafetcher=$datafetcher; build_log=$art.BuildLog; zip=$zipPath } -counts $counts -timestamp $tsIso
        Write-Host "ZIP: $zipPath"
        Write-Host $summaryCompact
        exit 1
    }

    $project = Find-HarnessProject $repoRoot
    if (-not $project) { $project = Find-ExecutableCsproj $repoRoot }

    if ($Simulate) {
        Write-Info "Simulate flag set: generating telemetry CSVs directly in artifact dir."
        # Write simulated CSVs directly into the artifact directory to ensure they are collected
        $ordersCsv = Join-Path $art.Dir 'orders.csv'
        $riskCsv = Join-Path $art.Dir 'risk_snapshots.csv'
        $teleCsv = Join-Path $art.Dir 'telemetry.csv'
        @(
            'id,symbol,side,qty,price,timestamp'
            ('1,EURUSD,BUY,1000,1.1000,' + (Get-Date).ToString('s'))
            ('2,EURUSD,SELL,500,1.1010,' + (Get-Date).ToString('s'))
            ('3,GBPUSD,BUY,200,1.2800,' + (Get-Date).ToString('s'))
        ) | Set-Content -Path $ordersCsv -Encoding UTF8
        @(
            'timestamp,equity,balance,margin,risk_state'
            ((Get-Date).ToString('s') + ',10000,10000,0,NORMAL')
        ) | Set-Content -Path $riskCsv -Encoding UTF8
        @(
            'timestamp,metric,value'
            ((Get-Date).ToString('s') + ',ticks_processed,12345')
        ) | Set-Content -Path $teleCsv -Encoding UTF8
    } elseif ($project) {
        # Direct harness output into the artifact directory via environment variables
        $runEnv = @{ BOTG_LOG_PATH = $art.Dir; BOTG_MODE = 'paper'; BOTG_TELEMETRY_FLUSH_SEC = '2' }
        Start-Run -projectPath $project -durationSec $DurationSeconds -envVars $runEnv
    } else {
        Write-Warn "Harness not found and no runnable csproj detected. Re-run with -Simulate to emit sample telemetry."
        throw "No runnable project"
    }

    # Prefer artifact directory (we set BOTG_LOG_PATH to this for the run)
    $teleDir = $art.Dir
    if (-not (Test-Path $teleDir)) {
        Write-Warn "Artifact dir missing unexpectedly: $teleDir; falling back to detection."
        $teleDir = Ensure-TelemetryDir $repoRoot
    }

    $orders = if ($teleDir) { Join-Path $teleDir 'orders.csv' }
    $risk = if ($teleDir) { Join-Path $teleDir 'risk_snapshots.csv' }
    $telemetry = if ($teleDir) { Join-Path $teleDir 'telemetry.csv' }
    $datafetcher = if ($teleDir) { Join-Path $teleDir 'datafetcher.log' }

    foreach ($f in @($orders,$risk,$telemetry,$datafetcher,$art.BuildLog)) { Copy-IfExists $f $art.Dir }

    $ordersCount = Count-Lines (Join-Path $art.Dir 'orders.csv')
    $riskCount = Count-Lines (Join-Path $art.Dir 'risk_snapshots.csv')
    $telemetryCount = Count-Lines (Join-Path $art.Dir 'telemetry.csv')

    Compress-Archive -Path (Join-Path $art.Dir '*') -DestinationPath $art.Zip -Force

    $summaryCompact = Write-Summary -summaryPath $art.Summary -meta $meta -buildOk:$true -artifacts @{ orders=(Join-Path $art.Dir 'orders.csv'); risk=(Join-Path $art.Dir 'risk_snapshots.csv'); telemetry=(Join-Path $art.Dir 'telemetry.csv'); datafetcher=(Join-Path $art.Dir 'datafetcher.log'); build_log=$art.BuildLog; zip=$art.Zip } -counts @{ orders=$ordersCount; risk=$riskCount; telemetry=$telemetryCount } -timestamp $tsIso
    Write-Host "ZIP: $($art.Zip)"
    Write-Host $summaryCompact
    exit 0
}
catch {
    Write-Err $_
    exit 1
}
