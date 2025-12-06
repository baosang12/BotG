@echo off
setlocal ENABLEDELAYEDEXPANSION
chcp 65001 >nul
title BotG PatternLayer Deployment Helper

echo ========================================
echo   BotG PatternLayer - Trợ lý deployment
echo ========================================
echo.

where powershell >nul 2>nul
if %errorlevel% neq 0 (
    echo L^ỗi: Không tìm thấy PowerShell trong PATH.
    echo Cài đặt PowerShell rồi chạy lại.
    pause
    exit /b 1
)

:menu
echo [1] Kiểm tra môi trường cTrader
echo [2] Deploy BotG sang cTrader
echo [3] Cấu hình telemetry
echo [4] Xem deployment checklist
echo [5] Thoát
echo.
set /p choice="Chọn tuỳ chọn (1-5): "
echo.

if "%choice%"=="1" goto check_env
if "%choice%"=="2" goto deploy
if "%choice%"=="3" goto configure
if "%choice%"=="4" goto checklist
if "%choice%"=="5" goto exit

echo Lựa chọn không hợp lệ.
echo.
goto menu

:ensure_path
if "%CTRADER_PATH%"=="" (
    set /p CTRADER_PATH="Nhập đường dẫn cTrader (vd: C:\cTrader): "
    if "%CTRADER_PATH%"=="" (
        echo Chưa nhập đường dẫn.
        echo.
        pause
        goto menu
    )
)
if not exist "%CTRADER_PATH%" (
    echo Không tìm thấy thư mục: %CTRADER_PATH%
    echo.
    pause
    set CTRADER_PATH=
    goto menu
)
goto :eof

:check_env
call :ensure_path
call :detect_paths
echo === Kiểm tra môi trường cTrader ===
echo.

echo cTrader Path: %CTRADER_PATH%
if defined ROBOT_PARENT (
    echo ✓ Gốc cTrader documents: %ROBOT_PARENT%
) else (
    echo ✗ Không xác định được thư mục documents cTrader
)
if defined ROBOT_DIR (
    if exist "%ROBOT_DIR%" (
        echo ✓ Thư mục Robots: %ROBOT_DIR%
    ) else (
        echo ✗ Không tìm thấy thư mục Robots tại %ROBOT_DIR%
    )
) else (
    echo ✗ Chưa định vị được thư mục Robots
)
if defined LOG_DIR (
    if exist "%LOG_DIR%" (
        echo ✓ Thư mục log %LOG_DIR% tồn tại
    ) else (
        echo ✗ Chưa có thư mục log %LOG_DIR%
    )
) else (
    echo ✗ Không xác định được thư mục log
)
if defined ROBOT_DIR (
    if exist "%ROBOT_DIR%\BotG.algo" (
        for %%A in ("%ROBOT_DIR%\BotG.algo") do set size=%%~zA
        echo ✓ BotG.algo sẵn có (!size! bytes)
    ) else (
        echo ✗ Chưa có BotG.algo trong Robots
    )
) else (
    echo ✗ Không thể kiểm tra BotG.algo
)
if defined CONFIG_FILE (
    if exist "%CONFIG_FILE%" (
        echo ✓ TrendAnalyzerConfig.json sẵn sàng (%CONFIG_FILE%)
    ) else (
        echo ✗ Thiếu TrendAnalyzerConfig.json (mong đợi tại %CONFIG_FILE%)
    )
) else (
    echo ✗ Không xác định được vị trí TrendAnalyzerConfig.json
)

echo.
pause
echo.
goto menu

:deploy
call :ensure_path
echo === Deploy BotG sang cTrader ===
echo.
powershell -ExecutionPolicy Bypass -File "%~dp0deploy-to-ctrader.ps1" -CTraderPath "%CTRADER_PATH%"
if %errorlevel% neq 0 (
    echo Deploy gặp lỗi (exit code %errorlevel%).
) else (
    echo Deploy hoàn tất.
)
echo.
pause
echo.
goto menu

:configure
call :ensure_path
call :detect_paths
echo === Thiết lập telemetry ===
echo.
if not exist "%CONFIG_FILE%" (
    echo Chưa có cấu hình. Tạo cấu hình mẫu...
    (
        echo {
        echo   "FeatureFlags": {
        echo     "UsePatternLayer": true
        echo   },
        echo   "PatternTelemetry": {
        echo     "EnablePatternLogging": true,
        echo     "LogDirectory": "d:\\botg\\logs\\patternlayer\\",
        echo     "EnableConsoleOutput": true,
        echo     "SampleRate": 1,
        echo     "EnableDebugMode": false
        echo   }
        echo }
    )>"%CONFIG_FILE%"
    echo Đã tạo file mới tại %CONFIG_FILE%
) else (
    echo File đã tồn tại: %CONFIG_FILE%
    echo Một số dòng tham chiếu:
    type "%CONFIG_FILE%" ^| findstr /C:"PatternTelemetry" /C:"UsePatternLayer"
)

echo.
echo Đảm bảo các khoá sau:
echo  - FeatureFlags.UsePatternLayer = true
echo  - PatternTelemetry.EnablePatternLogging = true
echo  - PatternTelemetry.LogDirectory = d:\botg\logs\patternlayer\
echo.
pause
echo.
goto menu

:checklist
echo === Checklist rút gọn ===
echo [ ] Build bằng scripts\build-release.ps1
echo [ ] Chạy deploy-to-ctrader.ps1
echo [ ] Kiểm tra BotG.algo và TrendAnalyzerConfig.json
echo [ ] Tạo d:\botg\logs\patternlayer\
echo [ ] Khởi động cTrader và xác minh telemetry
echo [ ] Kiểm tra CSV mới tạo
echo.
pause
echo.
goto menu

:detect_paths
set "ROBOT_PARENT="
set "ROBOT_DIR="
set "LOG_DIR=d:\botg\logs\patternlayer"
set "CONFIG_FILE="
if exist "%CTRADER_PATH%\Robots" (
    set "ROBOT_PARENT=%CTRADER_PATH%"
    set "ROBOT_DIR=%CTRADER_PATH%\Robots"
) else (
    if exist "%CTRADER_PATH%\cAlgo\Robots" (
        set "ROBOT_PARENT=%CTRADER_PATH%\cAlgo"
        set "ROBOT_DIR=%CTRADER_PATH%\cAlgo\Robots"
    )
)
if defined ROBOT_PARENT (
    if exist "%ROBOT_PARENT%\TrendAnalyzerConfig.json" (
        set "CONFIG_FILE=%ROBOT_PARENT%\TrendAnalyzerConfig.json"
    ) else (
        set "CONFIG_FILE=%ROBOT_PARENT%\Algo\TrendAnalyzerConfig.json"
    )
)
if not defined CONFIG_FILE (
    set "CONFIG_FILE=%CTRADER_PATH%\TrendAnalyzerConfig.json"
)
goto :eof

:exit
echo Cảm ơn đã sử dụng trợ lý triển khai.
pause
endlocal
exit /b 0
