#requires -Modules Pester
Import-Module (Join-Path $PSScriptRoot '..\scripts\lib_csv_merge.ps1') -Force

Describe "CSV Merge Functions" {
    BeforeEach {
        $TestDrive = "TestDrive:"
        $TempDir = Join-Path $TestDrive "csv_test"
        New-Item -ItemType Directory -Path $TempDir -Force | Out-Null
    }
    
    Context "Remove-Bom Function" {
        It "Should remove BOM from UTF-8 string" {
            $bomString = [char]0xFEFF + "test,data,here"
            $result = Remove-Bom $bomString
            $result | Should -Be "test,data,here"
        }
        
        It "Should handle null input" {
            $result = Remove-Bom $null
            $result | Should -Be $null
        }
        
        It "Should trim whitespace" {
            $result = Remove-Bom "  test,data  "
            $result | Should -Be "test,data"
        }
    }
    
    Context "Get-LineCount Function" {
        It "Should return 0 for non-existent file" {
            $result = Get-LineCount (Join-Path $TempDir "nonexistent.csv")
            $result | Should -Be 0
        }
        
        It "Should count lines in existing file" {
            $testFile = Join-Path $TempDir "test.csv"
            Set-Content -LiteralPath $testFile -Value @("header", "line1", "line2") -Encoding UTF8
            $result = Get-LineCount $testFile
            $result | Should -Be 3
        }
        
        It "Should return 0 for empty file" {
            $testFile = Join-Path $TempDir "empty.csv"
            Set-Content -LiteralPath $testFile -Value "" -Encoding UTF8
            $result = Get-LineCount $testFile
            $result | Should -Be 0
        }
    }
    
    Context "Write-StubOrders Function" {
        It "Should create stub with V3 header" {
            $stubFile = Join-Path $TempDir "stub.csv"
            Write-StubOrders -out $stubFile
            
            $stubFile | Should -Exist
            $content = Get-Content -LiteralPath $stubFile
            $content.Count | Should -Be 1
            $content[0] | Should -Match "status.*reason.*latency_ms.*price_requested.*price_filled"
            $content[0] | Should -Match "take_profit.*requested_units.*level.*risk_R_usd"
        }
    }
    
    Context "Assert-HeaderSchema Function" {
        It "Should pass with all required columns" {
            $validHeader = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            { Assert-HeaderSchema -header $validHeader } | Should -Not -Throw
        }
        
        It "Should throw with missing required columns" {
            $invalidHeader = "ts_iso,phase,status"
            { Assert-HeaderSchema -header $invalidHeader } | Should -Throw "*Missing required columns*"
        }
        
        It "Should warn about missing optional columns" {
            $partialHeader = "status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $warnings = @()
            Assert-HeaderSchema -header $partialHeader -WarningVariable warnings
            $warnings | Should -Not -BeNullOrEmpty
        }
    }
    
    Context "Merge-Orders Function" {
        It "Should create stub when no files provided" {
            $mergedFile = Join-Path $TempDir "merged.csv"
            Merge-Orders -ordersFiles @() -mergedOut $mergedFile
            
            $mergedFile | Should -Exist
            $content = Get-Content -LiteralPath $mergedFile
            $content.Count | Should -Be 1
            $content[0] | Should -Match "status.*reason.*latency_ms"
        }
        
        It "Should merge two valid CSV files with same header" {
            # Create test files
            $file1 = Join-Path $TempDir "orders1.csv"
            $file2 = Join-Path $TempDir "orders2.csv"
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            
            Set-Content -LiteralPath $file1 -Value @($header, "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100") -Encoding UTF8
            Set-Content -LiteralPath $file2 -Value @($header, "2025-01-02,FILL,OK,none,15,1.240,1.241,200,200") -Encoding UTF8
            
            $mergedFile = Join-Path $TempDir "merged.csv"
            Merge-Orders -ordersFiles @($file1, $file2) -mergedOut $mergedFile
            
            $content = Get-Content -LiteralPath $mergedFile
            $content.Count | Should -Be 3  # header + 2 data rows
            $content[0] | Should -Be $header
            $content[1] | Should -Match "2025-01-01.*FILL"
            $content[2] | Should -Match "2025-01-02.*FILL"
        }
        
        It "Should skip files with header mismatch" {
            $file1 = Join-Path $TempDir "orders1.csv"
            $file2 = Join-Path $TempDir "orders2.csv"
            $header1 = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $header2 = "different,header,structure"
            
            Set-Content -LiteralPath $file1 -Value @($header1, "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100") -Encoding UTF8
            Set-Content -LiteralPath $file2 -Value @($header2, "data,row,here") -Encoding UTF8
            
            $mergedFile = Join-Path $TempDir "merged.csv"
            $warnings = @()
            Merge-Orders -ordersFiles @($file1, $file2) -mergedOut $mergedFile -WarningVariable warnings
            
            $content = Get-Content -LiteralPath $mergedFile
            $content.Count | Should -Be 2  # header + 1 data row (file2 skipped)
            $warnings | Should -Not -BeNullOrEmpty
        }
        
        It "Should handle BOM in files" {
            $file1 = Join-Path $TempDir "orders_bom.csv"
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $bomHeader = [char]0xFEFF + $header
            
            Set-Content -LiteralPath $file1 -Value @($bomHeader, "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100") -Encoding UTF8
            
            $mergedFile = Join-Path $TempDir "merged.csv"
            Merge-Orders -ordersFiles @($file1) -mergedOut $mergedFile
            
            $content = Get-Content -LiteralPath $mergedFile
            $content[0] | Should -Be $header  # BOM should be removed
        }
        
        It "Should handle Unicode paths with diacritics" {
            $unicodeDir = Join-Path $TempDir "Tài_liệu_test"
            New-Item -ItemType Directory -Path $unicodeDir -Force | Out-Null
            
            $file1 = Join-Path $unicodeDir "orders_unicode.csv"
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            
            Set-Content -LiteralPath $file1 -Value @($header, "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100") -Encoding UTF8
            
            $mergedFile = Join-Path $TempDir "merged_unicode.csv"
            { Merge-Orders -ordersFiles @($file1) -mergedOut $mergedFile } | Should -Not -Throw
            
            $mergedFile | Should -Exist
        }
    }
    
    Context "Write-RunReport Function" {
        It "Should handle empty CSV (no data)" {
            $mergedFile = Join-Path $TempDir "empty_merged.csv"
            $closedFile = Join-Path $TempDir "closed.csv"
            Write-StubOrders -out $mergedFile
            
            $runRoot = $TempDir
            Write-RunReport -runRoot $runRoot -mergedOut $mergedFile -closedOut $closedFile
            
            $reportFile = Join-Path $runRoot "report_60m.md"
            $reportFile | Should -Exist
            $content = Get-Content -LiteralPath $reportFile -Raw
            $content | Should -Match "no data"
            $content | Should -Match "FIFO: N/A"
        }
        
        It "Should calculate latency statistics for FILL records" {
            $mergedFile = Join-Path $TempDir "filled_merged.csv"
            $closedFile = Join-Path $TempDir "closed.csv"
            $header = "ts_iso,phase,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled"
            $data = @(
                $header,
                "2025-01-01,FILL,OK,none,10,1.234,1.235,100,100",
                "2025-01-02,FILL,OK,none,20,1.240,1.241,200,200",
                "2025-01-03,REQUEST,OK,none,5,1.250,1.251,300,300"
            )
            Set-Content -LiteralPath $mergedFile -Value $data -Encoding UTF8
            Set-Content -LiteralPath $closedFile -Value "dummy" -Encoding UTF8  # Create closed file
            
            $runRoot = $TempDir
            Write-RunReport -runRoot $runRoot -mergedOut $mergedFile -closedOut $closedFile
            
            $reportFile = Join-Path $runRoot "report_60m.md"
            $content = Get-Content -LiteralPath $reportFile -Raw
            $content | Should -Match "FILL: 2"
            $content | Should -Match "avg=15"  # (10+20)/2
            $content | Should -Match "min=10"
            $content | Should -Match "max=20"
            $content | Should -Match "FIFO: OK"
        }
    }
}