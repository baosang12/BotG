#requires -Version 5.1

BeforeAll {
    $script:originalLocation = Get-Location
    Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
    
    # Check if Pester is available
    $script:pesterAvailable = $null -ne (Get-Module -ListAvailable -Name Pester)
    if (-not $script:pesterAvailable) {
        Write-Warning "Pester not installed - tests will be skipped"
        return
    }
}

AfterAll {
    if ($script:originalLocation) {
        Set-Location -LiteralPath $script:originalLocation
    }
}

Describe "ops.ps1 Operational Functions" -Skip:(-not $script:pesterAvailable) {
    
    BeforeAll {
        # Ensure we're in the right directory
        $opsPath = ".\scripts\ops.ps1"
        if (-not (Test-Path -LiteralPath $opsPath)) {
            throw "ops.ps1 not found at $opsPath"
        }
    }
    
    Context "Script Loading" {
        It "should load ops.ps1 without errors" {
            { . ".\scripts\ops.ps1" } | Should -Not -Throw
        }
        
        It "should define required functions" {
            . ".\scripts\ops.ps1"
            
            $requiredFunctions = @(
                'Invoke-Smoke60mRun',
                'Invoke-Smoke60mMergeLatest', 
                'Invoke-Smoke60mMergeExisting',
                'Show-Smoke60mReport',
                'Show-PhaseStats'
            )
            
            foreach ($func in $requiredFunctions) {
                Get-Command $func -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
            }
        }
    }
    
    Context "Function Execution" {
        BeforeAll {
            . ".\scripts\ops.ps1"
        }
        
        It "should execute Invoke-Smoke60mMergeLatest without throwing errors" {
            # This should not throw even if no runs exist
            { Invoke-Smoke60mMergeLatest -ErrorAction SilentlyContinue } | Should -Not -Throw
        }
        
        It "should execute Show-PhaseStats gracefully when no data exists" {
            # This should handle the case where no v2 runs exist
            { Show-PhaseStats -ErrorAction SilentlyContinue } | Should -Not -Throw
        }
        
        It "should execute Show-Smoke60mReport gracefully when no data exists" {
            # This should handle the case where no v2 runs exist  
            { Show-Smoke60mReport -ErrorAction SilentlyContinue } | Should -Not -Throw
        }
    }
    
    Context "File Operations" {
        BeforeAll {
            . ".\scripts\ops.ps1"
            
            # Check if we have any existing runs to test with
            $outRoot = "D:\botg\runs"
            $script:hasExistingRuns = $false
            
            if (Test-Path -LiteralPath $outRoot) {
                $latestRun = Get-ChildItem -LiteralPath $outRoot -Directory -Filter "paper_smoke_60m_v2_*" -ErrorAction SilentlyContinue |
                             Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($latestRun) {
                    $script:hasExistingRuns = $true
                    $script:latestRunPath = $latestRun.FullName
                }
            }
        }
        
        It "should create orders_merged.csv when data exists" -Skip:(-not $script:hasExistingRuns) {
            Invoke-Smoke60mMergeLatest
            
            $mergedCsv = Join-Path -LiteralPath $script:latestRunPath -ChildPath "orders_merged.csv"
            Test-Path -LiteralPath $mergedCsv | Should -Be $true
        }
        
        It "should create report_60m.md when data exists" -Skip:(-not $script:hasExistingRuns) {
            $reportMd = Join-Path -LiteralPath $script:latestRunPath -ChildPath "report_60m.md"
            Test-Path -LiteralPath $reportMd | Should -Be $true
        }
    }
}

Describe "ops_selftest.ps1 Self-Test Script" -Skip:(-not $script:pesterAvailable) {
    
    It "should exist and be executable" {
        Test-Path -LiteralPath ".\scripts\ops_selftest.ps1" | Should -Be $true
    }
    
    It "should execute without throwing errors" {
        { & ".\scripts\ops_selftest.ps1" } | Should -Not -Throw
    }
    
    It "should exit with code 0 on successful execution" {
        & ".\scripts\ops_selftest.ps1" | Out-Null
        $LASTEXITCODE | Should -Be 0
    }
}