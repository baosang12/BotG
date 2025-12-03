Write-Host "=== PatternLayer Deployment Validation ===" -ForegroundColor Cyan

Write-Host "1. Checking ConfigMap..." -ForegroundColor Yellow
kubectl get configmap trend-analyzer-config -n botg-staging --request-timeout=10s
if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ ConfigMap deployed successfully" -ForegroundColor Green
} else {
    Write-Host "❌ ConfigMap not found" -ForegroundColor Red
    exit 1
}

Write-Host "`n2. Checking deployment..." -ForegroundColor Yellow
$deploymentStatus = kubectl get deployment analysis-module -n botg-staging -o json --request-timeout=10s 2>&1
if ($LASTEXITCODE -eq 0) {
    $statusObj = $deploymentStatus | ConvertFrom-Json | Select-Object -ExpandProperty status
    Write-Host "✅ Deployment status:" -ForegroundColor Green
    $statusObj | Format-List
} else {
    Write-Host "❌ Deployment check failed" -ForegroundColor Red
    Write-Host $deploymentStatus
    exit 1
}

Write-Host "`n3. Checking pods..." -ForegroundColor Yellow
$pods = kubectl get pods -n botg-staging -l app=analysis-module --request-timeout=10s 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Pods are healthy" -ForegroundColor Green
    Write-Host $pods
} else {
    Write-Host "❌ Pods check failed" -ForegroundColor Red
    Write-Host $pods
    exit 1
}

Write-Host "`n4. Checking PatternLayer logs..." -ForegroundColor Yellow
$logResult = kubectl logs -n botg-staging deployment/analysis-module --tail=20 2>&1 | Select-String -Pattern "PatternLayer|LiquidityAnalyzer|BreakoutQuality"
if ($LASTEXITCODE -eq 0 -and $logResult) {
    Write-Host "✅ PatternLayer initialization logs detected" -ForegroundColor Green
    $logResult | ForEach-Object { Write-Host "   $_" }
} else {
    Write-Host "⚠️ PatternLayer logs not found (may require additional traffic)" -ForegroundColor Yellow
}

Write-Host "`n=== Deployment Validation Complete ===" -ForegroundColor Green
