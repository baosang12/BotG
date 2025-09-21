[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments=$true)][object[]]$Args)
$here = Split-Path -Parent $PSCommandPath
$target = Join-Path $here 'run_smoke_60m_wrapper_v2.ps1'
& $target @Args
exit $LASTEXITCODE
