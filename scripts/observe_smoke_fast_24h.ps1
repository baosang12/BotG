param(
    [string]$OutputDir,
    [string]$Branch = 'main',
    [string]$Workflow = '.github/workflows/smoke-on-pr.yml',
    [int]$Limit = 10,
    [int]$Hours = 24,
    [int]$IntervalMinutes = 120,
    [int]$Iterations = 0,
    [switch]$VerboseLogs
)

$ErrorActionPreference = 'Stop'

function New-ObservationDirectory {
    param(
        [string]$BaseDir
    )

    if (-not $BaseDir) {
        $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $BaseDir = Join-Path 'path_issues/observe' ("smoke_fast_24h_{0}" -f $timestamp)
    }

    if (-not (Test-Path $BaseDir)) {
        New-Item -ItemType Directory -Path $BaseDir | Out-Null
    }

    return (Get-Item $BaseDir).FullName
}

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if (-not $Values -or $Values.Count -eq 0) {
        return $null
    }

    $ordered = $Values | Sort-Object
    $k = ($ordered.Count - 1) * $Percentile
    $f = [math]::Floor($k)
    $c = [math]::Ceiling($k)

    if ($f -eq $c) {
        return [math]::Round($ordered[$k], 2)
    }

    $lower = $ordered[$f]
    $upper = $ordered[$c]
    $result = $lower + ($k - $f) * ($upper - $lower)
    return [math]::Round($result, 2)
}

function Get-RunDetails {
    param(
        [long]$RunId
    )

    $detail = gh run view $RunId --json status,conclusion,createdAt,updatedAt,url,headBranch,headSha,jobs | ConvertFrom-Json

    $durationSeconds = ([datetime]$detail.updatedAt - [datetime]$detail.createdAt).TotalSeconds

    $artifactNames = @()
    try {
        $artifactNames = gh api "repos/$($env:GH_REPO)/actions/runs/$RunId/artifacts" --jq '.artifacts[].name' 2>$null
    } catch {
        $artifactNames = @()
    }
    $artifactNames = @($artifactNames)
    $eventsArtifact = $artifactNames -contains ("workflow-events-{0}" -f $RunId)

    $shadowJob = $detail.jobs | Where-Object { $_.name -eq 'shadow' } | Select-Object -First 1
    $concurrencyLines = @()
    $timeoutHits = @()
    $jobsWithTimeout = @()

    if ($shadowJob) {
        $jobLog = gh run view $RunId --job $shadowJob.databaseId --log
        $concurrencyLines = ($jobLog | Select-String 'Concurrency group:|Cancel-in-progress:' | ForEach-Object { $_.Line.Trim() })
    }

    foreach ($job in $detail.jobs) {
        $jobTimedOut = $false
        if ($job.conclusion -eq 'timed_out') {
            $jobTimedOut = $true
        } else {
            try {
                $jobLog = gh run view $RunId --job $job.databaseId --log
                $timeoutPattern = $jobLog | Select-String -Pattern 'timed out|Timeout reached|timeout exceeded' -SimpleMatch
                if ($timeoutPattern) {
                    $jobTimedOut = $true
                    $timeoutHits += [pscustomobject]@{
                        job      = $job.name
                        evidence = ($timeoutPattern | Select-Object -First 1).Line.Trim()
                    }
                }
            } catch {
                # ignore log retrieval failures
            }
        }

        if ($jobTimedOut) {
            $jobsWithTimeout += $job.name
        }
    }

    return [pscustomobject]@{
        id                 = $RunId
        url                = $detail.url
        created_at         = $detail.createdAt
        updated_at         = $detail.updatedAt
        duration_seconds   = [math]::Round($durationSeconds, 2)
        conclusion         = $detail.conclusion
        head_branch        = $detail.headBranch
        head_sha           = $detail.headSha
        events_artifact    = $eventsArtifact
        concurrency_lines  = $concurrencyLines
        timeout_jobs       = $jobsWithTimeout
        timeout_evidence   = $timeoutHits
    }
}

function Write-TrendFiles {
    param(
        [string]$OutputDir,
        [pscustomobject]$IterationSummary
    )

    $trendPath = Join-Path $OutputDir 'trend.json'
    $trendMdPath = Join-Path $OutputDir 'trend.md'

    $existing = @()
    if (Test-Path $trendPath) {
        $raw = Get-Content $trendPath -Raw
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $existing = $raw | ConvertFrom-Json
        }
    }

    $existing = @($existing)
    $existing += $IterationSummary
    $existing | ConvertTo-Json -Depth 6 | Out-File $trendPath -Encoding utf8

    $mdLines = @()
    $mdLines += "# Observation $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
    $mdLines += ""
    $mdLines += "| Metric | Value |"
    $mdLines += "| --- | --- |"
    $mdLines += "| Total runs | {0} |" -f $IterationSummary.total_runs
    $mdLines += "| Success | {0} |" -f $IterationSummary.success_count
    $mdLines += "| Failure | {0} |" -f $IterationSummary.failure_count
    $mdLines += "| Success rate | {0:P2} |" -f $IterationSummary.success_rate
    $mdLines += "| Duration p50 (s) | {0} |" -f ($IterationSummary.duration_percentiles.p50)
    $mdLines += "| Duration p95 (s) | {0} |" -f ($IterationSummary.duration_percentiles.p95)
    $mdLines += "| Timeout failures | {0} |" -f $IterationSummary.timeout_failures.Count
    $mdLines += ""
    $runIds = $IterationSummary.runs | ForEach-Object { $_.id } | Sort-Object
    $mdLines += "Runs inspected: {0}" -f ([string]::Join(', ', $runIds))
    $mdLines += ""

    $mdLines | Out-File -FilePath $trendMdPath -Append -Encoding utf8
}

function Get-GoNoGoStatus {
    param(
        [System.Collections.IEnumerable]$TrendEntries
    )

    if (-not $TrendEntries -or $TrendEntries.Count -eq 0) {
        return 'UNKNOWN'
    }

    $hasTimeout = $TrendEntries | Where-Object { $_.timeout_failures.Count -gt 0 }
    if ($hasTimeout) {
        return 'NO-GO'
    }

    $hasLowSuccess = $TrendEntries | Where-Object { $_.success_rate -lt 0.8 }
    if ($hasLowSuccess) {
        return 'WATCH'
    }

    return 'GO'
}

if (-not $env:GH_REPO -or [string]::IsNullOrWhiteSpace($env:GH_REPO)) {
    $env:GH_REPO = gh repo view --json nameWithOwner --jq '.nameWithOwner'
}

$OutputDir = New-ObservationDirectory -BaseDir $OutputDir
Write-Host "Observation directory: $OutputDir"

$trendPath = Join-Path $OutputDir 'trend.json'
if (-not (Test-Path $trendPath)) {
    '[]' | Out-File $trendPath -Encoding utf8
}

$summaryPath = Join-Path $OutputDir 'summary.json'

$iteration = 0
$deadline = (Get-Date).AddHours($Hours)

while ($true) {
    if ($Iterations -gt 0 -and $iteration -ge $Iterations) { break }
    if ((Get-Date) -ge $deadline) { break }

    $iteration++
    $iterationTimestamp = Get-Date
    Write-Host ("[{0}] Collecting smoke-fast-on-pr runs..." -f $iterationTimestamp.ToString("s"))

    $iterationDir = Join-Path $OutputDir ("iteration_{0}" -f $iterationTimestamp.ToString('yyyyMMdd_HHmmss'))
    New-Item -ItemType Directory -Force -Path $iterationDir | Out-Null

    $runList = gh run list --workflow $Workflow --branch $Branch --limit $Limit --json databaseId,status,conclusion,createdAt,updatedAt,url | ConvertFrom-Json
    $runList = @($runList)

    $runSummaries = @()
    foreach ($run in $runList) {
        $runDetails = Get-RunDetails -RunId $run.databaseId
        $runSummaries += $runDetails
        $runDetails | ConvertTo-Json -Depth 5 | Out-File (Join-Path $iterationDir ("run_{0}.json" -f $run.databaseId)) -Encoding utf8
    }

    $durations = $runSummaries | ForEach-Object { $_.duration_seconds }
    $p50 = Get-Percentile -Values $durations -Percentile 0.5
    $p95 = Get-Percentile -Values $durations -Percentile 0.95

    $successCount = ($runSummaries | Where-Object { $_.conclusion -eq 'success' }).Count
    $failureRuns = $runSummaries | Where-Object { $_.conclusion -ne 'success' }
    $failureCount = $failureRuns.Count
    $totalRuns = $runSummaries.Count

    $timeoutFailures = @()
    foreach ($failure in $failureRuns) {
        if ($failure.timeout_jobs.Count -gt 0) {
            $timeoutFailures += [pscustomobject]@{
                id       = $failure.id
                jobs     = $failure.timeout_jobs
                evidence = $failure.timeout_evidence
            }
        }
    }

    $iterationSummary = [pscustomobject]@{
        timestamp             = $iterationTimestamp.ToString('o')
        total_runs            = $totalRuns
        success_count         = $successCount
        failure_count         = $failureCount
        success_rate          = if ($totalRuns -gt 0) { $successCount / $totalRuns } else { 0 }
        duration_percentiles  = [pscustomobject]@{
            p50 = $p50
            p95 = $p95
        }
        concurrency_guard_ok  = ($runSummaries | Where-Object { $_.conclusion -eq 'success' -and $_.concurrency_lines.Count -gt 0 }).Count
        events_artifact_ok    = ($runSummaries | Where-Object { $_.events_artifact }).Count
        timeout_failures      = $timeoutFailures
        runs                  = $runSummaries
    }

    $iterationSummary | ConvertTo-Json -Depth 6 | Out-File (Join-Path $iterationDir 'iteration_summary.json') -Encoding utf8

    Write-TrendFiles -OutputDir $OutputDir -IterationSummary $iterationSummary

    $existingTrend = Get-Content $trendPath -Raw | ConvertFrom-Json
    $goNoGo = Get-GoNoGoStatus -TrendEntries $existingTrend
    $overview = [pscustomobject]@{
        generated_at = (Get-Date).ToString('o')
        go_no_go     = $goNoGo
        iterations   = $existingTrend
    }
    $overview | ConvertTo-Json -Depth 6 | Out-File $summaryPath -Encoding utf8

    if ($Iterations -gt 0 -and $iteration -ge $Iterations) { break }

    $nextIteration = $iterationTimestamp.AddMinutes($IntervalMinutes)
    if ($nextIteration -gt $deadline) { break }

    $delaySeconds = [int][math]::Max(1, ($nextIteration - (Get-Date)).TotalSeconds)
    if ($VerboseLogs) {
        Write-Host ("Sleeping {0} seconds until next sample ({1})..." -f $delaySeconds, $nextIteration.ToString("s"))
    }
    Start-Sleep -Seconds $delaySeconds
}

Write-Host "Observation complete. Summary -> $summaryPath"
