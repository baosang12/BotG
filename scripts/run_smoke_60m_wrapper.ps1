# NOTE: Shim for CI. Real preflight/smoke takes precedence when present.
# On main, CI_BLOCK_FAIL=1 makes missing scripts fail CI.
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Args)
$here = Split-Path -Parent $PSCommandPath
$target = Join-Path $here 'run_smoke_60m_wrapper_v2.ps1'
& $target @Args
exit $LASTEXITCODE

