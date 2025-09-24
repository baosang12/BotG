#requires -Modules Pester

Describe "Wrapper v2 Integration Tests" {
    BeforeAll {
        $script:WrapperPath = Join-Path $PSScriptRoot '..\scripts\run_smoke_60m_wrapper_v2.ps1'
        $script:TestDrive = "TestDrive:"
        $script:TestOutRoot = Join-Path $TestDrive "wrapper_test_runs"
        $script:TestLogPath = Join-Path $TestDrive "wrapper_test_logs"
        
        New-Item -ItemType Directory -Path $script:TestOutRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $script:TestLogPath -Force | Out-Null
    }
    
    Context "Segment Pattern Discovery" {
        It "Should find orders.csv in seg01/seg02 pattern" {
            # Create test run with seg01/seg02 pattern
            $testRun = Join-Path $script:TestOutRoot "paper_smoke_60m_v2_20250920_test01"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            $seg01 = Join-Path $testRun "seg01"
            $seg02 = Join-Path $testRun "seg02"
            New-Item -ItemType Directory -Path $seg01, $seg02 -Force | Out-Null
            
            $tele01 = Join-Path $seg01 "telemetry"
            $tele02 = Join-Path $seg02 "telemetry"
            New-Item -ItemType Directory -Path $tele01, $tele02 -Force | Out-Null
            
            # Create valid orders.csv files
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $data1 = @($header, "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100")
            $data2 = @($header, "2025-01-02,FILL,OK,none,15,1.240,1.241,200,200")
            
            Set-Content -LiteralPath (Join-Path $tele01 "orders.csv") -Value $data1 -Encoding UTF8
            Set-Content -LiteralPath (Join-Path $tele02 "orders.csv") -Value $data2 -Encoding UTF8
            
            # Run wrapper in MergeOnly mode
            & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 2 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath | Out-Null
            
            # Check merged output
            $mergedFile = Join-Path $testRun "orders_merged.csv"
            $mergedFile | Should -Exist
            $content = Get-Content -LiteralPath $mergedFile
            $content.Count | Should -Be 3  # header + 2 data rows
        }
        
        It "Should find orders.csv in segment_1/segment_2 pattern" {
            # Create test run with segment_1/segment_2 pattern
            $testRun = Join-Path $script:TestOutRoot "paper_smoke_60m_v2_20250920_test02"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            $seg1 = Join-Path $testRun "segment_1"
            $seg2 = Join-Path $testRun "segment_2"
            New-Item -ItemType Directory -Path $seg1, $seg2 -Force | Out-Null
            
            $tele1 = Join-Path $seg1 "telemetry"
            $tele2 = Join-Path $seg2 "telemetry"
            New-Item -ItemType Directory -Path $tele1, $tele2 -Force | Out-Null
            
            # Create valid orders.csv files
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $data1 = @($header, "2025-01-01,FILL,OK,none,12,1.234,1.235,150,150")
            $data2 = @($header, "2025-01-02,FILL,OK,none,18,1.240,1.241,250,250")
            
            Set-Content -LiteralPath (Join-Path $tele1 "orders.csv") -Value $data1 -Encoding UTF8
            Set-Content -LiteralPath (Join-Path $tele2 "orders.csv") -Value $data2 -Encoding UTF8
            
            # Run wrapper in MergeOnly mode
            & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 2 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath | Out-Null
            
            # Check merged output
            $mergedFile = Join-Path $testRun "orders_merged.csv"
            $mergedFile | Should -Exist
            $content = Get-Content -LiteralPath $mergedFile
            $content.Count | Should -Be 3  # header + 2 data rows
        }
        
        It "Should handle mixed patterns (prioritize segment_X over segXX)" {
            # Create test run with both patterns (should prefer segment_X)
            $testRun = Join-Path $script:TestOutRoot "paper_smoke_60m_v2_20250920_test03"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            $seg1 = Join-Path $testRun "segment_1"
            $seg01 = Join-Path $testRun "seg01"
            New-Item -ItemType Directory -Path $seg1, $seg01 -Force | Out-Null
            
            $tele1 = Join-Path $seg1 "telemetry"
            $tele01 = Join-Path $seg01 "telemetry"
            New-Item -ItemType Directory -Path $tele1, $tele01 -Force | Out-Null
            
            # Create different content to verify which is used
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $dataSegment = @($header, "2025-01-01,FILL,OK,segment_1,10,1.234,1.235,100,100")
            $dataSeg = @($header, "2025-01-01,FILL,OK,seg01,10,1.234,1.235,100,100")
            
            Set-Content -LiteralPath (Join-Path $tele1 "orders.csv") -Value $dataSegment -Encoding UTF8
            Set-Content -LiteralPath (Join-Path $tele01 "orders.csv") -Value $dataSeg -Encoding UTF8
            
            # Run wrapper
            & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 1 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath | Out-Null
            
            # Should use segment_1 (not seg01)
            $mergedFile = Join-Path $testRun "orders_merged.csv"
            $content = Get-Content -LiteralPath $mergedFile
            $content[1] | Should -Match "segment_1"
            $content[1] | Should -Not -Match "seg01"
        }
    }
    
    Context "FIFO Processing" {
        It "Should skip FIFO when CSV has only header" {
            # Create test run with header-only CSV
            $testRun = Join-Path $script:TestOutRoot "paper_smoke_60m_v2_20250920_test04"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            $seg01 = Join-Path $testRun "seg01"
            New-Item -ItemType Directory -Path $seg01 -Force | Out-Null
            
            # Create empty orders.csv (header only)
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            Set-Content -LiteralPath (Join-Path $seg01 "orders.csv") -Value $header -Encoding UTF8
            
            # Run wrapper
            $output = & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 1 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath 2>&1 | Out-String
            
            # Should skip FIFO
            $output | Should -Match "No data rows.*Skipping FIFO"
            
            # Report should show FIFO: N/A
            $reportFile = Join-Path $testRun "report_60m.md"
            $reportContent = Get-Content -LiteralPath $reportFile -Raw
            $reportContent | Should -Match "FIFO: N/A"
        }
        
        It "Should run FIFO when CSV has data" {
            # Create test run with real data
            $testRun = Join-Path $script:TestOutRoot "paper_smoke_60m_v2_20250920_test05"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            $seg01 = Join-Path $testRun "seg01"
            New-Item -ItemType Directory -Path $seg01 -Force | Out-Null
            
            # Create orders.csv with FILL data
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $data = @(
                $header,
                "2025-01-01T10:00:00,FILL,OK,none,10,1.234,1.235,100,100",
                "2025-01-01T10:01:00,FILL,OK,none,15,1.240,1.241,200,200"
            )
            Set-Content -LiteralPath (Join-Path $seg01 "orders.csv") -Value $data -Encoding UTF8
            
            # Mock the Python script to avoid dependency issues in test
            # Create a simple Python script that creates the expected output
            $mockPython = @"
import sys
import argparse
parser = argparse.ArgumentParser()
parser.add_argument('--orders')
parser.add_argument('--out')
parser.add_argument('--encoding')
args = parser.parse_args()

# Create mock closed trades output
with open(args.out, 'w', encoding='utf-8') as f:
    f.write('trade_id,open_time,close_time,symbol,side,volume,open_price,close_price,pnl,status\\n')
    f.write('1,2025-01-01T10:00:00,2025-01-01T10:01:00,EURUSD,BUY,100,1.234,1.241,0.7,CLOSED\\n')

print('FIFO reconstruction completed')
"@
            $mockPythonFile = Join-Path $testRun "mock_reconstruct.py"
            Set-Content -LiteralPath $mockPythonFile -Value $mockPython -Encoding UTF8
            
            # Temporarily replace the Python script
            $originalPython = "reconstruct_closed_trades_sqlite.py"
            $backupPython = "reconstruct_closed_trades_sqlite.py.test_backup"
            Copy-Item $originalPython $backupPython -ErrorAction SilentlyContinue
            Copy-Item $mockPythonFile $originalPython
            
            try {
                # Run wrapper
                $output = & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 1 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath 2>&1 | Out-String
                
                # Should run FIFO
                $output | Should -Match "FIFO reconstruction completed"
                
                # Check closed trades file exists
                $closedFile = Join-Path $testRun "closed_trades_fifo_reconstructed.csv"
                $closedFile | Should -Exist
                
                # Report should show FIFO: OK
                $reportFile = Join-Path $testRun "report_60m.md"
                $reportContent = Get-Content -LiteralPath $reportFile -Raw
                $reportContent | Should -Match "FIFO: OK"
            }
            finally {
                # Restore original Python script
                Copy-Item $backupPython $originalPython -ErrorAction SilentlyContinue
                Remove-Item $backupPython -ErrorAction SilentlyContinue
            }
        }
    }
    
    Context "Error Handling" {
        It "Should handle missing segments gracefully" {
            # Create test run without any segment directories
            $testRun = Join-Path $script:TestOutRoot "paper_smoke_60m_v2_20250920_test06"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            # Run wrapper (should create stub)
            $output = & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 2 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath 2>&1 | Out-String
            
            # Should create merged file with stub
            $mergedFile = Join-Path $testRun "orders_merged.csv"
            $mergedFile | Should -Exist
            $content = Get-Content -LiteralPath $mergedFile
            $content.Count | Should -Be 1  # header only
            
            # Should have warnings about missing files
            $output | Should -Match "orders.csv not found"
        }
        
        It "Should handle Unicode paths correctly" {
            # Create test run with Unicode characters in path
            $unicodeTestRoot = Join-Path $script:TestOutRoot "Tài_liệu_test"
            New-Item -ItemType Directory -Path $unicodeTestRoot -Force | Out-Null
            
            $testRun = Join-Path $unicodeTestRoot "paper_smoke_60m_v2_20250920_unicode"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            $seg01 = Join-Path $testRun "seg01"
            New-Item -ItemType Directory -Path $seg01 -Force | Out-Null
            
            # Create valid orders.csv
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $data = @($header, "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100")
            Set-Content -LiteralPath (Join-Path $seg01 "orders.csv") -Value $data -Encoding UTF8
            
            # Run wrapper (should not crash on Unicode paths)
            { & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 1 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath } | Should -Not -Throw
            
            # Check output exists
            $mergedFile = Join-Path $testRun "orders_merged.csv"
            $mergedFile | Should -Exist
        }
    }
    
    Context "Report Generation" {
        It "Should generate proper report with latency statistics" {
            # Create test run with FILL records
            $testRun = Join-Path $script:TestOutRoot "paper_smoke_60m_v2_20250920_test07"
            New-Item -ItemType Directory -Path $testRun -Force | Out-Null
            
            $seg01 = Join-Path $testRun "seg01"
            $seg02 = Join-Path $testRun "seg02"
            New-Item -ItemType Directory -Path $seg01, $seg02 -Force | Out-Null
            
            # Create orders.csv with varying latencies
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $data1 = @(
                $header,
                "2025-01-01,REQUEST,OK,none,5,1.234,1.235,100,100",
                "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100"
            )
            $data2 = @(
                $header,
                "2025-01-02,REQUEST,OK,none,8,1.240,1.241,200,200",
                "2025-01-02,FILL,OK,none,20,1.240,1.241,200,200",
                "2025-01-03,FILL,OK,none,30,1.250,1.251,300,300"
            )
            
            Set-Content -LiteralPath (Join-Path $seg01 "orders.csv") -Value $data1 -Encoding UTF8
            Set-Content -LiteralPath (Join-Path $seg02 "orders.csv") -Value $data2 -Encoding UTF8
            
            # Run wrapper
            & $script:WrapperPath -MergeOnly -ExistingRoot $testRun -Segments 2 -OutRoot $script:TestOutRoot -LogPath $script:TestLogPath
            
            # Check report content
            $reportFile = Join-Path $testRun "report_60m.md"
            $reportContent = Get-Content -LiteralPath $reportFile -Raw
            
            $reportContent | Should -Match "FILL: 3"  # 3 FILL records
            $reportContent | Should -Match "REQUEST=2.*FILL=3"  # Phase breakdown
            $reportContent | Should -Match "avg=20"  # (10+20+30)/3
            $reportContent | Should -Match "min=10"
            $reportContent | Should -Match "max=30"
        }
    }
}