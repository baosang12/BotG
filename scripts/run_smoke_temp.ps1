param([int]$DurationMinutes = 2, [string]$LogPath = "D:\botg\logs", [string]$OutDir = "")

$ErrorActionPreference = "Stop"
$env:BOTG_LOG_PATH = $LogPath
Write-Output "=== SMOKE ${DurationMinutes}m STARTED ==="
Write-Output "LogPath: $LogPath"
Write-Output "OutDir: $OutDir" 
Write-Output "Started at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

# Tạo mock orders.csv với header V3 để test
if ($OutDir) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $mockOrders = Join-Path $OutDir "orders.csv"
    $header = "phase,timestamp_iso,epoch_ms,order_id,intended_price,stop_loss,exec_price,theoretical_lots,theoretical_units,requested_volume,filled_size,slippage_points,broker_msg,client_order_id,side,action,type,status,reason,latency_ms,price_requested,price_filled,size_requested,size_filled,take_profit,requested_units,level,risk_R_usd,session,host,timestamp_request,timestamp_ack,timestamp_fill"
    
    # Add some mock data
    $mockData = @(
        "REQUEST,2025-09-20T10:00:00.000Z,1726826400000,ABC12345,2650.50,2640.00,,0.01,1000,0.01,,,,ABC12345,BUY,OPEN,MARKET,REQUEST,SIGNAL_EXECUTION,0,2650.50,,0.01,,2655.00,1000,1,10.50,session1,host1,,,"
        "ACK,2025-09-20T10:00:00.100Z,1726826400100,ABC12345,2650.50,2640.00,2650.45,0.01,1000,0.01,,,,ABC12345,BUY,OPEN,MARKET,ACK,ORDER_ACCEPTED,100,2650.50,2650.45,0.01,,2655.00,1000,1,10.50,session1,host1,,,"
        "FILL,2025-09-20T10:00:00.150Z,1726826400150,ABC12345,2650.50,2640.00,2650.45,0.01,1000,0.01,1000,,,ABC12345,BUY,OPEN,MARKET,FILL,ORDER_FILLED,150,2650.50,2650.45,0.01,1000,2655.00,1000,1,10.50,session1,host1,,,"
    )
    
    ($header + "`n" + ($mockData -join "`n")) | Out-File $mockOrders -Encoding UTF8
    Write-Output " Mock orders.csv created: $mockOrders"
}

Start-Sleep ($DurationMinutes * 10) # Mô phỏng chạy (10 giây = 1 phút mock)
Write-Output "=== SMOKE ${DurationMinutes}m COMPLETED ==="
