#!/usr/bin/env pwsh
<#!
.SYNOPSIS
    Dọn các artifact cục bộ (file build, file thử nghiệm, thư mục tạm) trong repo BotG.

.DESCRIPTION
    Script gom các file/ thư mục phát sinh khi build, thử nghiệm tay hoặc sao lưu thủ công
    và xóa chúng khỏi repo làm việc để kiến trúc sạch hơn. Có thể chạy ở chế độ DryRun
    để xem trước những gì sẽ bị xóa.

.PARAMETER DryRun
    Khi bật, script chỉ log danh sách sẽ xóa mà không đụng vào file.
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

$explicitFiles = @(
    "BotG.zip",
    "build_report.txt",
    "temp_b64.txt",
    "temp_closed.cs",
    "temp_exec.cs",
    "temp_gate24h.yml",
    "temp_gate2_run.ps1",
    "temp_risk.cs",
    "temp_risk_main.cs",
    "temp_selftest.yml",
    "test.txt",
    "test_create.txt",
    "test_closed_trades.csv"
)

$globPatterns = @(
    "temp_*.log",
    "temp_*.json"
)

$directories = @(
    "_tmp",
    "tmp_untracked_backups"
)

$targets = @()

foreach ($file in $explicitFiles) {
    $fullPath = Join-Path $repoRoot $file
    if (Test-Path $fullPath) {
        $targets += [pscustomobject]@{ Path = $fullPath; Type = 'File' }
    }
}

foreach ($pattern in $globPatterns) {
    Get-ChildItem -Path $repoRoot -Filter $pattern -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $targets += [pscustomobject]@{ Path = $_.FullName; Type = 'File' }
        }
}

foreach ($dir in $directories) {
    $fullPath = Join-Path $repoRoot $dir
    if (Test-Path $fullPath) {
        $targets += [pscustomobject]@{ Path = $fullPath; Type = 'Directory' }
    }
}

if ($targets.Count -eq 0) {
    Write-Host "✅ Không tìm thấy artifact cần dọn." -ForegroundColor Green
    return
}

$targets = $targets | Sort-Object Path -Unique

function Get-RelativePath([string]$base, [string]$child) {
    $normalized = $child.Substring($base.Length).TrimStart('\\','/')
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return '.'
    }
    return $normalized
}

foreach ($item in $targets) {
    $relative = Get-RelativePath $repoRoot $item.Path
    if ($DryRun) {
        Write-Host "[DRY-RUN] $($item.Type) $relative" -ForegroundColor Yellow
        continue
    }

    if ($item.Type -eq 'Directory') {
        Remove-Item -Path $item.Path -Recurse -Force
    } else {
        Remove-Item -Path $item.Path -Force
    }
    Write-Host "🧹 Đã xóa $relative" -ForegroundColor Green
}

Write-Host "🎯 Hoàn tất dọn dẹp." -ForegroundColor Cyan
