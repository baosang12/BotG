param([string]$root="D:\botg")
$sentinels = @(
  (Join-Path $root "RUN_STOP"),
  (Join-Path $root "RUN_PAUSE"),
  (Join-Path $root "sentinel\RUN_STOP"),
  (Join-Path $root "sentinel\RUN_PAUSE")
) | Where-Object { $_ }  # filter null

$result = [pscustomobject]@{
  RUN_STOP = Test-Path $sentinels[0]
  RUN_PAUSE = Test-Path $sentinels[1]
  RUN_STOP_ALT = Test-Path $sentinels[2]
  RUN_PAUSE_ALT = Test-Path $sentinels[3]
  ANY = (Test-Path $sentinels[0]) -or (Test-Path $sentinels[1]) -or (Test-Path $sentinels[2]) -or (Test-Path $sentinels[3])
}
$result | ConvertTo-Json -Depth 5
