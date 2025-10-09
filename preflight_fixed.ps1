$ErrorActionPreference = "Stop"
$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$OUT = "path_issues\preflight_$ts"
New-Item -ItemType Directory -Force $OUT | Out-Null
$LOG = Join-Path $OUT "preflight_stdout.log"
$JSON = Join-Path $OUT "preflight_report.json"

# Banner start
Write-Host ("[PRE-FLIGHT START] {0} (UTC+7)" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))

# Helper function
function Write-Log($msg) { 
    $msg | Tee-Object -FilePath $LOG -Append | Write-Host 
}

# Set default repo
gh repo set-default baosang12/BotG | Out-Null

Write-Log "== Repo HEAD =="
$headRaw = gh api repos/baosang12/BotG/commits/main
$headObj = $headRaw | ConvertFrom-Json
Write-Log "SHA: $($headObj.sha)"
Write-Log "Message: $($headObj.commit.message)"
Write-Log "Date: $($headObj.commit.author.date)"

Write-Log "== CI status (required checks) =="
$checksRaw = gh api repos/baosang12/BotG/commits/main/check-runs
$checksObj = ($checksRaw | ConvertFrom-Json).check_runs
Write-Log "Found $($checksObj.Count) check runs"
foreach($check in $checksObj) {
    Write-Log "  $($check.name): $($check.status)/$($check.conclusion)"
}

Write-Log "== gate24h.yml snapshot =="
$wfPath = ".github/workflows/gate24h.yml"
Copy-Item $wfPath (Join-Path $OUT "workflow_snap.yml") -Force
$yaml = Get-Content $wfPath -Raw

$hasTimeout = $yaml -match 'timeout-minutes:\s*1500'
$hasAlways = $yaml -match 'if:\s*\$\{\{\s*always\(\)\s*\}\}'
$hasSelfcheck = $yaml -match 'artifact-selfcheck'
Write-Log "timeout-minutes:1500? -> $hasTimeout"
Write-Log "upload-artifact has always()? -> $hasAlways" 
Write-Log "artifact-selfcheck job present? -> $hasSelfcheck"

Write-Log "== Self-hosted runners (repo level) =="
$runnersRaw = gh api repos/baosang12/BotG/actions/runners
$runnersObj = $runnersRaw | ConvertFrom-Json
Write-Log "Total runners: $($runnersObj.total_count)"
foreach($runner in $runnersObj.runners) {
    Write-Log "  $($runner.name): $($runner.status) ($($runner.os))"
}

Write-Log "== Disk free (GB) =="
$diskC = [math]::Round((Get-PSDrive C).Free/1GB,2)
$diskD = [math]::Round((Get-PSDrive D).Free/1GB,2)
Write-Log "C: $diskC GB free; D: $diskD GB free"

Write-Log "== Sentinels =="
$stop = Test-Path D:\botg\logs\RUN_STOP
$pause = Test-Path D:\botg\logs\RUN_PAUSE
Write-Log "RUN_STOP=$stop; RUN_PAUSE=$pause"
if (!(Test-Path D:\botg\logs)) { 
    New-Item -ItemType Directory -Force D:\botg\logs | Out-Null 
}
# Test write permissions
$tmp = "D:\botg\logs\_preflight_write_test.txt"
"ok" | Out-File -FilePath $tmp -Encoding utf8
Remove-Item $tmp -Force
Write-Log "Write permissions: OK"

# Selfcheck (optional)
$didSelfcheck = $false
$runId = $null
if ($hasSelfcheck) {
    Write-Log "== Trigger artifact-selfcheck (workflow_dispatch) =="
    gh workflow run gate24h.yml -f mode=paper -f hours=0 -f source=artifact-selfcheck | Out-Null
    Start-Sleep -Seconds 8
    
    $runsRaw = gh run list --workflow "gate24h.yml" -L 5 --json databaseId,status,displayTitle,createdAt
    $runsObj = $runsRaw | ConvertFrom-Json
    if ($runsObj -and $runsObj.Count -gt 0) {
        $runId = $runsObj[0].databaseId
        Write-Log "Selfcheck runId: $runId"
        
        # Wait up to 2 minutes
        $limit = (Get-Date).AddMinutes(2)
        do {
            Start-Sleep -Seconds 5
            $vRaw = gh run view $runId --json status,conclusion,updatedAt
            $vObj = $vRaw | ConvertFrom-Json
            Write-Log "Status: $($vObj.status) / $($vObj.conclusion)"
            $st = $vObj.status
        } while ($st -ne "completed" -and (Get-Date) -lt $limit)
        
        if ($st -eq "completed") {
            # Download and list artifacts
            $selfDst = Join-Path $OUT "selfcheck_artifacts"
            New-Item -ItemType Directory -Force $selfDst | Out-Null
            try {
                gh run download $runId -D $selfDst | Out-Null
                $artifactFiles = Get-ChildItem -Recurse $selfDst | Where-Object {!$_.PSIsContainer}
                $artifactFiles | ForEach-Object { $_.FullName } | Set-Content (Join-Path $OUT "selfcheck_artifacts_list.txt")
                $didSelfcheck = $true
                Write-Log "Downloaded $($artifactFiles.Count) artifact files"
            } catch {
                Write-Log "Failed to download artifacts: $($_.Exception.Message)"
            }
        }
    }
}

# Generate report
$report = [ordered]@{
    timestamp_local = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    repo_head = @{
        sha = $headObj.sha
        message = $headObj.commit.message
        author_date = $headObj.commit.author.date
    }
    ci_checks = $checksObj | Select-Object name,status,conclusion
    workflow = [ordered]@{
        timeout_1500 = $hasTimeout
        upload_always = $hasAlways
        has_artifact_selfcheck = $hasSelfcheck
    }
    runners = @{
        total_count = $runnersObj.total_count
        details = $runnersObj.runners | Select-Object name,status,os
    }
    disk_free_gb = @{ C = $diskC; D = $diskD }
    sentinels = @{ RUN_STOP = $stop; RUN_PAUSE = $pause }
    paths = @{ logs_root = "D:\botg\logs"; out_dir = $OUT }
    selfcheck = @{ executed = $didSelfcheck; run_id = $runId }
    pass_criteria = [ordered]@{
        disk_ge_10GB = ($diskD -ge 10 -or $diskC -ge 10)
        no_RUN_STOP = (-not $stop)
        timeout_set = $hasTimeout
        upload_always = $hasAlways
        runner_online = ($runnersObj.runners.status -contains "online")
    }
}

$report | ConvertTo-Json -Depth 6 | Out-File -FilePath $JSON -Encoding utf8

# Banner end
Write-Host ("[PRE-FLIGHT DONE] {0} (UTC+7) Report: {1}" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"), $JSON)