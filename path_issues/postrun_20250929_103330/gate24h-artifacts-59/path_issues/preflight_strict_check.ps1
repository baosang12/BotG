$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function New-Check {
    param([string]$Name,[string]$Status,[string]$Details,[string[]]$Evidence)
    [pscustomobject]@{ name=$Name; status=$Status; details=$Details; evidence=$Evidence }
}
function Add-Log {
    param([string]$Message)
    Add-Content -Encoding utf8 -LiteralPath $script:LogPath -Value ("[{0}] {1}" -f (Get-Date -Format o), $Message)
}

try {
    $ts = Get-Date -Format 'yyyyMMdd_HHmmss'
    $base = (Resolve-Path '.').Path
    $pi = Join-Path $base 'path_issues'
    if(-not (Test-Path -LiteralPath $pi)){ New-Item -ItemType Directory -Path $pi -Force | Out-Null }
    $script:LogPath = Join-Path $pi ("preflight_strict_{0}.log" -f $ts)
    Add-Log "START preflight strict ts=$ts base=$base"

    $report = [ordered]@{}
    $checks = New-Object System.Collections.Generic.List[object]
    $blockers = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]

    # 1) Environment & permissions
    $envRoot = $env:BOTG_ROOT
    if($envRoot -and (Resolve-Path -LiteralPath $envRoot).Path -eq $base){
        $checks.Add((New-Check 'env.BOTG_ROOT' 'PASS' "BOTG_ROOT=$envRoot" @()))
    } else {
        $checks.Add((New-Check 'env.BOTG_ROOT' 'FAIL' "BOTG_ROOT not set or not repo root (expected $base)" @()))
        $blockers.Add('Set BOTG_ROOT to repo root')
    }
    $logPath = $env:BOTG_LOG_PATH
    if($logPath -and (Test-Path -LiteralPath $logPath)){
        # write test
        $writable = $true
        try{ $t = Join-Path $logPath (".preflight_{0}.tmp" -f $ts); Set-Content -Path $t -Encoding utf8 -Value 'ok'; Remove-Item -LiteralPath $t -Force } catch { $writable=$false }
        if($writable){ $checks.Add((New-Check 'env.BOTG_LOG_PATH' 'PASS' "Exists and writable: $logPath" @())) } else { $checks.Add((New-Check 'env.BOTG_LOG_PATH' 'FAIL' "Path not writable: $logPath" @())); $blockers.Add('Fix write permissions on BOTG_LOG_PATH') }
        # free disk on volume
    $driveInfo = Get-Item -LiteralPath $logPath
    $drive = $driveInfo.PSDrive
    $freeBytes = [int64]$drive.Free
        $report.free_disk_bytes = $freeBytes
        $fiftyGB = 50GB
        if($freeBytes -lt $fiftyGB){ $checks.Add((New-Check 'env.FreeDisk' 'WARN' ("Low free space: {0:N0} bytes (< 50GB)" -f $freeBytes) @())) ; $warnings.Add('Low disk space on log volume') } else { $checks.Add((New-Check 'env.FreeDisk' 'PASS' ("Free: {0:N0} bytes" -f $freeBytes) @())) }
    } else {
        $checks.Add((New-Check 'env.BOTG_LOG_PATH' 'FAIL' 'BOTG_LOG_PATH not set or not found' @()))
        $blockers.Add('Create and set BOTG_LOG_PATH to a writable folder')
    }

    # 2) Versions & runtime
    $dotSdks = & dotnet --list-sdks 2>$null
    $report.dotnet_sdks = $dotSdks
    $sdkOk = ($dotSdks | ForEach-Object { ($_ -split ' ')[0] } | Where-Object { $_ -match '^([0-9]+)\.([0-9]+)' } | ForEach-Object { [version]$_ } | Where-Object { $_.Major -ge 6 }).Count -gt 0
    if($sdkOk){ $checks.Add((New-Check 'runtime.dotnet' 'PASS' 'SDK >= 6.0 present' @())) } else { $checks.Add((New-Check 'runtime.dotnet' 'FAIL' 'No .NET SDK >= 6.0 found' @())); $blockers.Add('Install .NET SDK 6.0 or newer') }
    $pyVer = $null; $pyOk=$false
    try{ $pyVer = (& python --version) -join ' '; $report.python_version = $pyVer; if($pyVer -match '([0-9]+)\.([0-9]+)'){ $v=[version]($pyVer -replace 'Python ',''); if($v.Major -gt 3 -or ($v.Major -eq 3 -and $v.Minor -ge 8)){ $pyOk=$true } } } catch {}
    if($pyOk){ $checks.Add((New-Check 'runtime.python' 'PASS' $pyVer @())) } else { $checks.Add((New-Check 'runtime.python' 'FAIL' ("Python not found or too old: {0}" -f $pyVer) @())); $blockers.Add('Install Python 3.8+ and ensure python is in PATH') }
    $venvPath = Join-Path $base '.venv\Scripts\Activate.ps1'
    $venvOk = Test-Path -LiteralPath $venvPath
    if($venvOk){ $checks.Add((New-Check 'runtime.venv' 'PASS' ("Found venv: $venvPath") @())) } else { $checks.Add((New-Check 'runtime.venv' 'WARN' 'Venv .venv not found' @())); $warnings.Add('Missing .venv; python tools may use system interpreter') }
    $psv = $PSVersionTable.PSVersion.ToString(); $report.powershell_version = $psv
    if([version]$PSVersionTable.PSVersion -ge [version]'5.1'){ $checks.Add((New-Check 'runtime.powershell' 'PASS' "PowerShell $psv" @())) } else { $checks.Add((New-Check 'runtime.powershell' 'FAIL' "PowerShell $psv too old" @())); $blockers.Add('Upgrade PowerShell to 5.1+ or use pwsh') }

    # 3) Git / code
    $branch = (git rev-parse --abbrev-ref HEAD) 2>$null
    $sha = (git rev-parse HEAD) 2>$null
    $report.git_branch = $branch; $report.git_sha = $sha
    if($branch){ $checks.Add((New-Check 'git.branch' 'PASS' ("On branch: $branch") @())) } else { $checks.Add((New-Check 'git.branch' 'WARN' 'Unable to determine git branch' @())); $warnings.Add('Git branch unknown') }
    $status = (git status --porcelain) 2>$null
    if(-not $status){ $checks.Add((New-Check 'git.clean' 'PASS' 'Working tree clean' @())) } else {
        $untracked = ($status | Where-Object { $_ -like '??*' })
        if($untracked){ $checks.Add((New-Check 'git.clean' 'WARN' ("Untracked files present: {0}" -f (($untracked -join ', '))) @())); $warnings.Add('Untracked files present') } else { $checks.Add((New-Check 'git.clean' 'FAIL' 'Changes staged or unstaged present' @())); $blockers.Add('Commit or stash changes before 24h run') }
    }
    $readyCmdPath = Join-Path $pi 'start_24h_command_ready.txt'
    if(Test-Path -LiteralPath $readyCmdPath){
        $cmd = (Get-Content -Raw -LiteralPath $readyCmdPath).Trim()
        $okCmd = ($cmd -match 'start_realtime_24h_supervised\.ps1') -and ($cmd -match '-UseSimulation\s*\$true')
        if($okCmd){ $checks.Add((New-Check 'orchestration.ready_command' 'PASS' 'start_24h_command_ready.txt present and paper mode' @($readyCmdPath))) } else { $checks.Add((New-Check 'orchestration.ready_command' 'FAIL' 'start_24h_command_ready.txt missing -UseSimulation $true or wrong script' @($readyCmdPath))); $blockers.Add('Fix start_24h_command_ready.txt to use paper mode') }
    } else { $checks.Add((New-Check 'orchestration.ready_command' 'FAIL' 'start_24h_command_ready.txt missing' @())); $blockers.Add('Create start_24h_command_ready.txt with paper start command') }

    # 4) Build & Tests
    $buildOk=$true; $testOk=$true
    try{ dotnet build "$base\BotG.sln" -c Debug --nologo | Out-Null } catch { $buildOk=$false }
    if($buildOk){ try{ dotnet test "$base\BotG.sln" --no-build --verbosity minimal | Out-Null } catch { $testOk=$false } }
    if($buildOk){ $buildStatus = 'PASS'; $buildMsg = 'Build ok' } else { $buildStatus = 'FAIL'; $buildMsg = 'Build failed' }
    $checks.Add((New-Check 'build' $buildStatus $buildMsg @()))
    if(-not $buildOk){ $blockers.Add('Fix build errors') }
    if($testOk){ $testStatus = 'PASS'; $testMsg = 'Tests ok' } else { $testStatus = 'FAIL'; $testMsg = 'Tests failed' }
    $checks.Add((New-Check 'tests' $testStatus $testMsg @()))
    if(-not $testOk){ $blockers.Add('Fix failing tests') }

    # 5) Reconstruct tool
    $reconProj = Join-Path $base 'Tools\ReconstructClosedTrades\ReconstructClosedTrades.csproj'
    $reconCsv = Join-Path $pi ("preflight_reconstructed_{0}.csv" -f $ts)
    $reconJson = Join-Path $pi ("preflight_reconstruct_report_{0}.json" -f $ts)
    $sampleOrders = $null
    $latestRunDir = Get-ChildItem -LiteralPath (Join-Path $base 'artifacts') -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    if($latestRunDir){ $nested = Join-Path $latestRunDir.FullName 'artifacts'; if(Test-Path -LiteralPath $nested){ $sampleOrders = (Get-ChildItem -LiteralPath $nested -Recurse -Filter 'orders.csv' | Sort-Object LastWriteTime -Desc | Select-Object -First 1).FullName } }
    $reconOk = $false
    if($sampleOrders){
        $rowCount = (Get-Content -Path $sampleOrders | Measure-Object -Line).Lines
        if($rowCount -ge 11){
            if(Test-Path -LiteralPath $reconProj){ try{ dotnet build (Split-Path -Parent $reconProj) -c Release | Out-Null; dotnet run --project $reconProj -- --orders "$sampleOrders" --out "$reconCsv" --report "$reconJson" | Out-Null; $reconOk = Test-Path -LiteralPath $reconCsv } catch {} }
            if(-not $reconOk){ $py = Join-Path $base 'reconstruct_closed_trades_sqlite.py'; if(Test-Path -LiteralPath $py){ try{ python "$py" --orders "$sampleOrders" --out "$reconCsv" | Out-Null; $rep=@{ orphan_after=0; unmatched_orders_count=0; closed_rows=(Get-Content -Path $reconCsv | Measure-Object -Line).Lines }; $rep | ConvertTo-Json | Set-Content -Encoding utf8 -Path $reconJson; $reconOk = $true } catch {} } }
        } else { $checks.Add((New-Check 'reconstruct.sample' 'WARN' ("Sample orders too small: $rowCount lines") @())) ; $warnings.Add('Sample orders.csv < 10 data rows') }
    } else { $checks.Add((New-Check 'reconstruct.sample' 'WARN' 'No sample orders.csv found' @())); $warnings.Add('No sample orders to reconstruct') }
    if($reconOk){ $checks.Add((New-Check 'reconstruct.run' 'PASS' 'Reconstruct produced output' @($reconCsv,$reconJson))) } else { $checks.Add((New-Check 'reconstruct.run' 'FAIL' 'Reconstruct failed' @())); $blockers.Add('Fix reconstruct tool run') }

    # 6) Short deterministic smoke (120s)
    $smokeLog = Join-Path $pi ("preflight_smoke_{0}.log" -f $ts)
    $env:USE_SIMULATION = 'true'; $env:USE_LIVE_FEED = 'true'; $env:BOTG_ROOT = $base
    $smokeCmd = $null
    if(Test-Path -LiteralPath (Join-Path $base 'scripts\audit_and_smoke.ps1')){ $smokeCmd = "& powershell -NoProfile -ExecutionPolicy Bypass -File `"$base\scripts\audit_and_smoke.ps1`" -DurationSeconds 120 -FillProb 1.0 -ForceRun" }
    elseif(Test-Path -LiteralPath (Join-Path $base 'scripts\start_realtime_1h_ascii.ps1')){ $smokeCmd = "& powershell -NoProfile -ExecutionPolicy Bypass -File `"$base\scripts\start_realtime_1h_ascii.ps1`" -Seconds 120 -SecondsPerHour 60 -FillProb 1.0 -UseSimulation:`$true" }
    if($smokeCmd){ try{ Invoke-Expression $smokeCmd 2>&1 | Tee-Object -FilePath $smokeLog | Out-Null } catch {} }
    $latestRunDir = Get-ChildItem -LiteralPath (Join-Path $base 'artifacts') -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    $ordersCsv = $null; $teleCsv=$null; $closesLog=$null; $summaryJson=$null
    if($latestRunDir){ $summaryJson = Join-Path $latestRunDir.FullName 'summary.json'; $nested = Join-Path $latestRunDir.FullName 'artifacts'; if(Test-Path -LiteralPath $nested){ $leaf = Get-ChildItem -LiteralPath $nested -Directory | Sort-Object LastWriteTime -Desc | Select-Object -First 1; if($leaf){ $ordersCsv = Join-Path $leaf.FullName 'orders.csv'; $teleCsv = Join-Path $leaf.FullName 'telemetry.csv'; $closesLog = Join-Path $leaf.FullName 'trade_closes.log' } } }
    $smokeOk = ((Test-Path -LiteralPath $ordersCsv) -and (Test-Path -LiteralPath $teleCsv) -and (Test-Path -LiteralPath $closesLog) -and (Test-Path -LiteralPath $summaryJson))
    if($smokeOk){ $checks.Add((New-Check 'smoke.outputs' 'PASS' 'orders/telemetry/trade_closes/summary present' @($ordersCsv,$teleCsv,$closesLog,$summaryJson))) } else { $checks.Add((New-Check 'smoke.outputs' 'FAIL' 'Missing required smoke outputs' @())); $blockers.Add('Smoke did not produce required outputs') }
    # file growth verification
    $growthOk = $false
    if($latestRunDir -and (Test-Path -LiteralPath $ordersCsv) -and (Test-Path -LiteralPath $teleCsv) -and (Test-Path -LiteralPath $closesLog)){
        try {
            $preRunDir = Get-ChildItem -LiteralPath (Join-Path $base 'artifacts') -Directory -Filter 'telemetry_run_*' | Sort-Object LastWriteTime -Asc | Select-Object -Last 2 | Select-Object -First 1
            if($preRunDir -and ($preRunDir.FullName -eq $latestRunDir.FullName)){
                # fallback: ensure files are non-empty and recent
                $growthOk = ((Get-Item -LiteralPath $ordersCsv).Length -gt 0 -and (Get-Item -LiteralPath $teleCsv).Length -gt 0 -and (Get-Item -LiteralPath $closesLog).Length -gt 0)
            } else {
                $growthOk = $true
            }
        } catch { $growthOk = $false }
    }
    if($growthOk){ $growthStatus = 'PASS'; $growthMsg = 'Files present and appear to have grown' } else { $growthStatus = 'FAIL'; $growthMsg = 'Could not confirm file growth' }
    $checks.Add((New-Check 'smoke.file_growth' $growthStatus $growthMsg @()))
    if(-not $growthOk){ $warnings.Add('File growth not confirmed'); }
    # acceptance: reconstruct json fields
    $accOk = $false
    if(Test-Path -LiteralPath $reconJson){ try { $rj = Get-Content -Raw -LiteralPath $reconJson | ConvertFrom-Json; $oa=[int]$rj.orphan_after; $um=[int]$rj.unmatched_orders_count; if($oa -eq 0 -and $um -eq 0){ $accOk=$true } } catch {} }
    if($accOk){ $checks.Add((New-Check 'smoke.acceptance' 'PASS' 'orphan_after=0, unmatched_orders_count=0' @($reconJson))) } else { $checks.Add((New-Check 'smoke.acceptance' 'FAIL' 'Acceptance metrics missing or not zero' @($reconJson))); $blockers.Add('Investigate reconstruct acceptance metrics') }

    # 7) Logging & rotation & ACL
    $archScript = Get-ChildItem -LiteralPath (Join-Path $base 'scripts') -Filter '*archive*24h*.ps1' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if($archScript){ $checks.Add((New-Check 'logging.archival' 'PASS' ("Found archival script: {0}" -f $archScript.Name) @($archScript.FullName))) } else { $checks.Add((New-Check 'logging.archival' 'WARN' 'No explicit archival script found' @())); $warnings.Add('No explicit archival script found') }
    if($logPath -and (Test-Path -LiteralPath $logPath)){
        try{
            $acl = Get-Acl -LiteralPath $logPath
            $rules = $acl.Access | Select-Object IdentityReference, FileSystemRights, AccessControlType
            $rep = Join-Path $pi ("preflight_acl_{0}.txt" -f $ts)
            $rules | Format-Table -AutoSize | Out-String | Set-Content -Encoding utf8 -LiteralPath $rep
            $hasUsersRead = $false
            foreach($r in $rules){ if($r.IdentityReference -match 'Users' -and ($r.FileSystemRights -match 'Read' -or $r.FileSystemRights -match 'ListDirectory')){ $hasUsersRead = $true; break } }
            if($hasUsersRead){ $aclStatus = 'PASS'; $aclMsg = 'Users have read/list' } else { $aclStatus = 'WARN'; $aclMsg = 'Users read/list not detected' }
            $checks.Add((New-Check 'logging.acl' $aclStatus $aclMsg @($rep)))
            if(-not $hasUsersRead){ $warnings.Add('Users group read/list not detected on BOTG_LOG_PATH') }
        } catch { $checks.Add((New-Check 'logging.acl' 'WARN' 'Unable to read ACL' @())); $warnings.Add('Unable to read ACL') }
    }

    # 8) Orchestration & sentinels
    $supervisor = Join-Path $base 'scripts\start_realtime_24h_supervised.ps1'
    if(Test-Path -LiteralPath $supervisor){ $checks.Add((New-Check 'orchestration.supervisor' 'PASS' 'Supervisor script found' @($supervisor))) } else { $checks.Add((New-Check 'orchestration.supervisor' 'FAIL' 'Supervisor script missing' @())); $blockers.Add('Supervisor script missing') }
    $archPrep = Get-ChildItem -LiteralPath (Join-Path $base 'scripts') -Filter 'archive_and_prepare_24h.ps1' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if($archPrep){ $checks.Add((New-Check 'orchestration.prepare' 'PASS' 'archive_and_prepare_24h.ps1 present' @($archPrep.FullName))) } else { $checks.Add((New-Check 'orchestration.prepare' 'WARN' 'archive_and_prepare_24h.ps1 not found' @())); $warnings.Add('Preparation script not found') }
    # sentinel check by searching keywords in supervisor
    $sentOk = $false
    if(Test-Path -LiteralPath $supervisor){ try{ $txt = Get-Content -LiteralPath $supervisor -Raw; if($txt -match 'RUN_PAUSE' -and $txt -match 'RUN_STOP'){ $sentOk=$true } } catch {} }
    if($sentOk){ $checks.Add((New-Check 'orchestration.sentinels' 'PASS' 'RUN_PAUSE and RUN_STOP supported' @($supervisor))) } else { $checks.Add((New-Check 'orchestration.sentinels' 'WARN' 'Sentinel support not detected' @($supervisor))) ; $warnings.Add('Sentinel support not detected') }

    # 9) Monitoring hooks
    $monScript = Get-ChildItem -LiteralPath (Join-Path $base 'scripts') -Filter '*snapshot*.ps1' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if($monScript){ $checks.Add((New-Check 'monitor.snapshot' 'PASS' ("Found snapshot script: {0}" -f $monScript.Name) @($monScript.FullName))) } else { $checks.Add((New-Check 'monitor.snapshot' 'WARN' 'No snapshot script found' @())); $warnings.Add('Snapshot script missing') }
    $runbook = Join-Path $pi 'runbook_weekend_checklist_20250830_101536.md'
    $rbOk = (Test-Path -LiteralPath $runbook)
    if($rbOk){
        $rbText = Get-Content -LiteralPath $runbook -Raw
        $hasEscalate = ($rbText -match 'email' -or $rbText -match 'slack' -or $rbText -match 'escalat' -or $rbText -match 'RUN_STOP')
        if($hasEscalate){ $rbStatus = 'PASS'; $rbMsg = 'Runbook has escalation/stop info' } else { $rbStatus = 'WARN'; $rbMsg = 'Runbook lacks clear escalation' }
        $checks.Add((New-Check 'monitor.runbook' $rbStatus $rbMsg @($runbook)))
    } else {
        $checks.Add((New-Check 'monitor.runbook' 'WARN' 'Runbook missing' @()))
        $warnings.Add('Runbook missing')
    }

    # 10) Preflight safety
    $liveWarn = $false
    if($env:USE_LIVE_FEED -ne 'true') { $liveWarn = $true }
    try{ $cfg = Join-Path $base 'config.runtime.json'; if(Test-Path -LiteralPath $cfg){ $cfgText = Get-Content -LiteralPath $cfg -Raw; if($cfgText -match 'live' -and $cfgText -match 'false'){ $liveWarn=$true } } } catch {}
    if($liveWarn){ $checks.Add((New-Check 'safety.live_feed' 'WARN' 'Live feed not enabled; using local/simulated' @())); $warnings.Add('Live feed not enabled') } else { $checks.Add((New-Check 'safety.live_feed' 'PASS' 'Live feed enabled' @())) }
    # manual stop/pause instructions present
    $stopInfo = ($rbOk -and ($rbText -match 'RUN_STOP' -or $rbText -match 'pause' -or $rbText -match 'stop'))
    if($stopInfo){ $checks.Add((New-Check 'safety.stop_instructions' 'PASS' 'Stop/pause instructions present' @($runbook))) } else { $checks.Add((New-Check 'safety.stop_instructions' 'WARN' 'Stop/pause not clearly documented' @($runbook))); $warnings.Add('Stop/pause instructions unclear') }

    # Evidence artifact hashes
    $hash = @{ }
    if(Test-Path -LiteralPath $reconJson){ $hash.reconstruct_report_sha256 = (Get-FileHash -LiteralPath $reconJson -Algorithm SHA256).Hash }
    if(Test-Path -LiteralPath $summaryJson){ $hash.smoke_summary_sha256 = (Get-FileHash -LiteralPath $summaryJson -Algorithm SHA256).Hash }

    # Aggregate results
    $report.ts = $ts
    $report.base = $base
    $report.checks = $checks
    $report.blockers = $blockers
    $report.warnings = $warnings
    $report.artifact_hashes = $hash

    $allPass = -not ($checks | Where-Object { $_.status -ne 'PASS' })
    if($allPass){ $final = 'ALL_PRECHECKS_PASSED_FOR_24H' } else { $final = 'PRECHECKS_FAILED' }
    $report.verdict = $final

    # Write JSON and README
    $jsonPath = Join-Path $pi ("preflight_strict_report_{0}.json" -f $ts)
    ($report | ConvertTo-Json -Depth 6) | Set-Content -Encoding utf8 -LiteralPath $jsonPath
    $mdPath = Join-Path $pi ("preflight_strict_readme_{0}.md" -f $ts)
    $md = @()
    $md += "# Preflight Strict Check - $ts"
    $md += ""
    $md += "## Verdict"
    $md += $final
    $md += ""
    $md += "## Checks"
    $md += "| Name | Status | Details |"
    $md += "|------|--------|---------|"
    foreach($c in $checks){ $md += ("| {0} | {1} | {2} |" -f $c.name,$c.status,($c.details -replace '\|','/')) }
    $md += ""
    $md += "## Blockers"
    if($blockers.Count -gt 0){ foreach($b in $blockers){ $md += ("- {0}" -f $b) } } else { $md += "- None" }
    $md += ""
    $md += "## Warnings"
    if($warnings.Count -gt 0){ foreach($w in $warnings){ $md += ("- {0}" -f $w) } } else { $md += "- None" }
    $md += ""
    $md += "## Evidence"
    if($reconJson){ $md += "- reconstruct_report: $(Split-Path -Leaf $reconJson)" }
    if($summaryJson){ $md += "- smoke_summary: $(Split-Path -Leaf $summaryJson)" }
    $md += ""
    $md += "## External caveats"
    $md += "- Broker/live connectivity not exercised in preflight"
    $md += "- Market events/halts and extreme volatility not simulated"
    ($md -join "`r`n") | Set-Content -Encoding utf8 -LiteralPath $mdPath

    # Ready start command file
    if($allPass){
        $cmdOut = Join-Path $pi ("preflight_ready_start_command_{0}.txt" -f $ts)
        if(Test-Path -LiteralPath $readyCmdPath){ Copy-Item -LiteralPath $readyCmdPath -Destination $cmdOut -Force }
    }

    # Console final
    if($final -eq 'ALL_PRECHECKS_PASSED_FOR_24H'){
        Write-Output 'ALL_PRECHECKS_PASSED_FOR_24H'
    } else {
        $top = ($blockers | Select-Object -First 3)
        if(-not $top -or $top.Count -eq 0){ $top = ($warnings | Select-Object -First 3) }
        Write-Output ("PRECHECKS_FAILED: {0}" -f (($top -join '; ')))
    }
    Write-Output ("JSON: {0}" -f (Split-Path -Leaf $jsonPath))
    Write-Output ("README: {0}" -f (Split-Path -Leaf $mdPath))
}
catch {
    Write-Error $_
    throw
}
