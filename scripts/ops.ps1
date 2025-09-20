#requires -Version 5.1
Set-StrictMode -Version Latest

function Invoke-Smoke60mMergeLatest {
    param(
        [string]$OutRoot = "D:\botg\runs",
        [string]$LogPath = "D:\botg\logs"
    )
    
    & (Find-SmokeWrapper) -MergeOnly -OutRoot $OutRoot -LogPath $LogPath
}

function Show-PhaseStats {
    param([string]$OutRoot = "D:\botg\runs")
    
    $latestDir = Get-ChildItem "$OutRoot\paper_smoke_60m_v2_*" | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    if ($null -eq $latestDir) {
        "PHASES: No matching directories found"
        return
    }
    $root = $latestDir.FullName
    $merged = Join-Path $root 'orders_merged.csv'
    
    if (Test-Path $merged) {
        $csv = Import-Csv $merged
        "PHASES: " + (($csv | Group-Object phase | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join '; ')
    } else {
        "PHASES: No merged CSV found"
    }
}
