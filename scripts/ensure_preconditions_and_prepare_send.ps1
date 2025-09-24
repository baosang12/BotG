#Requires -Version 5.1

<#
.SYNOPSIS
    An toàn kiểm tra và chuẩn bị tất cả preconditions cho real order sending
.DESCRIPTION
    Script này liên tục kiểm tra và cố gắng hoàn tất tất cả điều kiện cần thiết để sẵn sàng
    gửi order thật. KHÔNG BAO GIỜ tự động gửi order thật - chỉ chuẩn bị và tạo lệnh.
.PARAMETER IntervalSeconds
    Khoảng nghỉ giữa các lần kiểm tra (default: 30)
.PARAMETER MaxAttempts
    Số lần thử tối đa (0 = unlimited, default: 0)
.PARAMETER AutoApply
    Cho phép script áp dụng một số fix an toàn (cần xác nhận interactive)
.PARAMETER BackupDir
    Thư mục backup (default: .\path_issues\ensure_preconditions_<ts>\backups)
.PARAMETER Verbose
    Hiển thị thông tin chi tiết
.PARAMETER NotifyWebhook
    URL webhook để thông báo khi sẵn sàng (optional)
.PARAMETER ApplyCode
    Cho phép áp dụng code patches (cần xác nhận bổ sung)
.PARAMETER Force
    Bỏ qua một số safety checks (chỉ dùng với AutoApply)
#>

[CmdletBinding()]
param(
    [int]$IntervalSeconds = 30,
    [int]$MaxAttempts = 0,
    [switch]$AutoApply,
    [string]$BackupDir = "",
    [switch]$Verbose,
    [string]$NotifyWebhook = "",
    [switch]$ApplyCode,
    [switch]$Force
)

# Global variables
$Global:ScriptStartTime = Get-Date
$Global:Timestamp = $Global:ScriptStartTime.ToString("yyyyMMdd_HHmmss")
$Global:ArtifactDir = ".\path_issues\ensure_preconditions_$Global:Timestamp"
$Global:RunLogPath = "$Global:ArtifactDir\run_log.txt"
$Global:AttemptCounter = 0
$Global:BackoffMultiplier = @{}

# Safety constraint: NEVER execute real send command
$Global:SAFETY_NEVER_SEND_REAL_ORDERS = $true

# Decision codes
$Global:DecisionCodes = @{
    READY = "READY"
    WAITING = "WAITING"
    AUTO_APPLIED = "AUTO_APPLIED"
    CANNOT_PROCEED = "CANNOT_PROCEED"
    ERROR = "ERROR"
    STOPPED = "STOPPED"
    MAX_ATTEMPTS_REACHED = "MAX_ATTEMPTS_REACHED"
    IMPLEMENTED_OK = "IMPLEMENTED_OK"
    CANNOT_RUN_ERROR = "CANNOT_RUN_ERROR"
}

function Initialize-ArtifactDirectory {
    if (-not (Test-Path $Global:ArtifactDir)) {
        New-Item -ItemType Directory -Path $Global:ArtifactDir -Force | Out-Null
    }
    
    if ([string]::IsNullOrEmpty($BackupDir)) {
        $Script:BackupDir = "$Global:ArtifactDir\backups"
    } else {
        $Script:BackupDir = $BackupDir.Replace("<ts>", $Global:Timestamp)
    }
    
    if (-not (Test-Path $Script:BackupDir)) {
        New-Item -ItemType Directory -Path $Script:BackupDir -Force | Out-Null
    }
    
    # Initialize log file
    "=== ensure_preconditions_and_prepare_send.ps1 Started ===" | Out-File $Global:RunLogPath -Encoding UTF8
    "Start Time: $Global:ScriptStartTime" | Out-File $Global:RunLogPath -Append -Encoding UTF8
    "Parameters: IntervalSeconds=$IntervalSeconds, MaxAttempts=$MaxAttempts, AutoApply=$AutoApply" | Out-File $Global:RunLogPath -Append -Encoding UTF8
    "" | Out-File $Global:RunLogPath -Append -Encoding UTF8
}

function Write-LogAndConsole {
    param([string]$Message, [string]$Level = "INFO")
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logEntry = "[$timestamp] [$Level] $Message"
    
    Write-Host $logEntry
    $logEntry | Out-File $Global:RunLogPath -Append -Encoding UTF8
}

function Test-ConfigRuntimeJson {
    $result = @{
        Name = "config.runtime.json"
        Passed = $false
        Details = @{}
        Issues = @()
        CanAutoFix = $false
    }
    
    $configPath = ".\config.runtime.json"
    
    if (-not (Test-Path $configPath)) {
        $result.Issues += "File không tồn tại: $configPath"
        $result.CanAutoFix = $true
        return $result
    }
    
    try {
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        $result.Details.ConfigExists = $true
        $result.Details.ConfigValid = $true
        
        # Check enable_real_send
        if ($null -eq $config.enable_real_send) {
            $result.Issues += "Thiếu property 'enable_real_send'"
            $result.CanAutoFix = $true
        } elseif ($config.enable_real_send -ne $true) {
            $result.Issues += "enable_real_send = $($config.enable_real_send), cần = true"
            $result.CanAutoFix = $true
        } else {
            $result.Details.EnableRealSend = $true
        }
        
        # Check use_live_broker
        if ($config.use_live_broker -eq $true) {
            $result.Issues += " NGUY HIỂM: use_live_broker = true (phải false hoặc absent)"
            $result.Details.UseLiveBroker = $true
            $result.CanAutoFix = $Force  # Only with Force flag
        } else {
            $result.Details.UseLiveBroker = $false
        }
        
        $result.Details.Config = $config
        
        if ($result.Issues.Count -eq 0) {
            $result.Passed = $true
        }
        
    } catch {
        $result.Issues += "Lỗi parse JSON: $($_.Exception.Message)"
        $result.Details.ConfigValid = $false
    }
    
    return $result
}

function Invoke-AllChecks {
    $checks = @()
    
    Write-LogAndConsole " Bắt đầu kiểm tra tất cả preconditions..." -Level "INFO"
    
    $checks += Test-ConfigRuntimeJson
    
    $passedCount = ($checks | Where-Object { $_.Passed }).Count
    $totalCount = $checks.Count
    
    Write-LogAndConsole " Kết quả: $passedCount/$totalCount checks passed" -Level "INFO"
    
    return @{
        Checks = $checks
        PassedCount = $passedCount
        TotalCount = $totalCount
        AllPassed = ($passedCount -eq $totalCount)
    }
}

function Save-StatusSnapshot {
    param($CheckResults, $AttemptNumber)
    
    $status = @{
        Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        AttemptNumber = $AttemptNumber
        ScriptVersion = "1.0.0"
        Parameters = @{
            IntervalSeconds = $IntervalSeconds
            MaxAttempts = $MaxAttempts
            AutoApply = $AutoApply.IsPresent
            ApplyCode = $ApplyCode.IsPresent
            Force = $Force.IsPresent
        }
        Results = $CheckResults
        Environment = @{
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            OSVersion = [System.Environment]::OSVersion.ToString()
            WorkingDirectory = (Get-Location).Path
        }
    }
    
    $statusFile = "$Global:ArtifactDir\status_$($AttemptNumber.ToString('000')).json"
    $status | ConvertTo-Json -Depth 10 | Out-File $statusFile -Encoding UTF8
    
    Write-LogAndConsole " Lưu status snapshot: $statusFile"
}

function Write-DecisionFile {
    param([string]$Decision, [string]$Details = "")
    
    $decisionFile = "$Global:ArtifactDir\DECISION.txt"
    
    $content = @"
DECISION: $Decision
TIMESTAMP: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
ATTEMPT: $Global:AttemptCounter

$Details
"@
    
    $content | Out-File $decisionFile -Encoding UTF8
    Write-LogAndConsole " Decision file: $Decision" -Level "INFO"
}

# Dry run function for testing
function Start-DryRunTest {
    try {
        Initialize-ArtifactDirectory
        
        $Global:AttemptCounter = 1
        Write-LogAndConsole " DRY-RUN TEST: Script implementation validation" -Level "INFO"
        
        # Run checks
        $checkResults = Invoke-AllChecks
        
        # Save status snapshot
        Save-StatusSnapshot $checkResults $Global:AttemptCounter
        
        # Write decision
        Write-DecisionFile $Global:DecisionCodes.IMPLEMENTED_OK "Dry-run test completed successfully"
        
        Write-LogAndConsole " Dry-run test hoàn thành" -Level "INFO"
        return $Global:DecisionCodes.IMPLEMENTED_OK
        
    } catch {
        $errorDetails = "Exception: $($_.Exception.Message)`nStackTrace: $($_.ScriptStackTrace)"
        Write-LogAndConsole " Dry-run test failed: $errorDetails" -Level "ERROR"
        
        Write-DecisionFile $Global:DecisionCodes.CANNOT_RUN_ERROR $errorDetails
        "$errorDetails" | Out-File "$Global:ArtifactDir\error.txt" -Encoding UTF8
        
        return $Global:DecisionCodes.CANNOT_RUN_ERROR
    }
}

# For dry-run testing only
Start-DryRunTest
