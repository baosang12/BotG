param(
  [string]$Owner = $env:GH_OWNER,
  [string]$Repo  = $env:GH_REPO_NAME,
  [string]$ConfigPath = ".github/branch-protection/required_checks.json",
  [switch]$Apply
)

if (-not $Owner -or -not $Repo) {
  if ($env:GH_REPO) {
    $parts = $env:GH_REPO.Split("/")
    $Owner = $parts[0]; $Repo = $parts[1]
  } else {
    throw "Set GH_REPO (owner/repo) or pass -Owner/-Repo"
  }
}

if (-not (Test-Path $ConfigPath)) { throw "Config not found: $ConfigPath" }
$cfg = Get-Content $ConfigPath | ConvertFrom-Json
$branch = $cfg.branch

Write-Host "Reading current protection for $Owner/$Repo@$branch..."
$current = gh api "repos/$Owner/$Repo/branches/$branch/protection" --jq "." 2>$null
if ($LASTEXITCODE -ne 0) { 
  Write-Host "No protection or insufficient permissions to read branch protection settings."; 
  Write-Host "This may be expected when running with a limited GITHUB_TOKEN in CI workflows."; 
  $current = $null 
}

$desiredContexts = ($cfg.contexts | Sort-Object)
Write-Host "Desired contexts:`n  - $($desiredContexts -join "`n  - ")"

if ($Apply) {
  $body = @{
    required_status_checks = @{
      strict   = [bool]$cfg.strict
      contexts = $cfg.contexts
    }
    enforce_admins = [bool]$cfg.enforce_admins
    required_pull_request_reviews = $cfg.required_pull_request_reviews
    restrictions = $null
  } | ConvertTo-Json -Depth 6

  Write-Host "Applying protection via GitHub API..."
  $body | gh api -X PUT "repos/$Owner/$Repo/branches/$branch/protection" `
    -H "Accept: application/vnd.github+json" `
    --input - | Out-Null
  Write-Host "✅ Branch protection updated."
} else {
  if ($current) {
    $currentContexts = (gh api "repos/$Owner/$Repo/branches/$branch/protection" --jq '.required_status_checks.contexts[]?' 2>$null) | Sort-Object
    Write-Host "Current contexts:`n  - $($currentContexts -join "`n  - ")"
    $diffA = Compare-Object $desiredContexts $currentContexts | Where-Object {$_.SideIndicator -eq "<="} | Select-Object -ExpandProperty InputObject
    $diffB = Compare-Object $desiredContexts $currentContexts | Where-Object {$_.SideIndicator -eq "=>"} | Select-Object -ExpandProperty InputObject
    if ($diffA -or $diffB) {
      Write-Host "❌ Drift detected."
      if ($diffA) { Write-Host "Missing on server:`n  - $($diffA -join "`n  - ")" }
      if ($diffB) { Write-Host "Extra on server:`n  - $($diffB -join "`n  - ")" }
      exit 2
    } else {
      Write-Host "✅ No drift."
    }
  } else {
    Write-Host "⚠️ Cannot read branch protection settings."
    Write-Host "If this is a CI environment, this is expected due to limited token permissions."
    Write-Host "To apply configuration, run locally with admin token: ./scripts/admin/sync_branch_protection.ps1 -Apply"
    exit 3
  }
}