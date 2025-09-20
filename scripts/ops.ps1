#requires -Version 5.1
Set-StrictMode -Version Latest

function Invoke-Smoke60mMergeLatest {
    param(
        [string]$OutRoot = "D:\botg\runs",
        [string]$LogPath = "D:\botg\logs"
    )
    
    & ".\scripts\run_smoke_60m_wrapper_v2.ps1" -MergeOnly -OutRoot $OutRoot -LogPath $LogPath
}

function Show-PhaseStats {
    param([string]$OutRoot = "D:\botg\runs")
    
    $root = (Get-ChildItem "$OutRoot\paper_smoke_60m_v2_*" | Sort-Object LastWriteTime -Desc | Select-Object -First 1).FullName
    $merged = Join-Path $root 'orders_merged.csv'
    
    if (Test-Path $merged) {
        $csv = Import-Csv $merged
        "PHASES: " + (($csv | Group-Object phase | % { "$($_.Name)=$($_.Count)" }) -join '; ')
    } else {
        "PHASES: No merged CSV found"
    }
}
