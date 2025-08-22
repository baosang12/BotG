# Branch Protection Pre-commit Check (PowerShell)
# This script helps enforce branch protection rules locally before pushing

param(
    [switch]$Force = $false
)

function Write-Info($message) {
    Write-Host "[INFO] $message" -ForegroundColor Green
}

function Write-Warn($message) {
    Write-Host "[WARN] $message" -ForegroundColor Yellow
}

function Write-Error($message) {
    Write-Host "[ERROR] $message" -ForegroundColor Red
}

# Get current branch
try {
    $currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
} catch {
    Write-Error "Failed to get current branch. Are you in a git repository?"
    exit 1
}

$protectedBranches = @("main", "master", "develop")

# Check if we're on a protected branch
function Test-ProtectedBranch {
    return $protectedBranches -contains $currentBranch
}

# Check if trying to push to protected branch
function Test-DirectPushProtection {
    if (Test-ProtectedBranch) {
        Write-Error "You are on protected branch '$currentBranch'"
        Write-Error "Direct pushes to protected branches are not allowed!"
        Write-Error "Please create a feature branch and use pull requests:"
        Write-Host ""
        Write-Host "  git checkout -b feature/your-feature-name"
        Write-Host "  # make your changes"
        Write-Host "  git add ."
        Write-Host "  git commit -m 'Your commit message'"
        Write-Host "  git push origin feature/your-feature-name"
        Write-Host "  # then create a PR via GitHub web interface"
        Write-Host ""
        return $false
    }
    return $true
}

# Check for large files
function Test-LargeFiles {
    Write-Info "Checking for large files (>5MB)..."
    
    try {
        $stagedFiles = git diff --cached --name-only
        $largeFiles = @()
        
        foreach ($file in $stagedFiles) {
            if (Test-Path $file) {
                $size = (Get-Item $file).Length
                if ($size -gt 5MB) {
                    $largeFiles += $file
                }
            }
        }
        
        if ($largeFiles.Count -gt 0) {
            Write-Error "Large files detected (>5MB):"
            $largeFiles | ForEach-Object { Write-Host "  $_" }
            Write-Error "Consider using Git LFS for large files or removing them"
            return $false
        }
        
        Write-Info "No large files detected"
        return $true
    } catch {
        Write-Warn "Could not check for large files: $_"
        return $true
    }
}

# Check for sensitive files
function Test-SensitiveFiles {
    Write-Info "Checking for potentially sensitive files..."
    
    $sensitivePatterns = @(
        "\.env$",
        "\.key$",
        "\.pem$", 
        "\.p12$",
        "\.pfx$",
        "password",
        "config\.json$",
        "secrets\."
    )
    
    try {
        $stagedFiles = git diff --cached --name-only
        $foundSensitive = $false
        
        foreach ($pattern in $sensitivePatterns) {
            $matches = $stagedFiles | Where-Object { $_ -match $pattern }
            if ($matches) {
                Write-Warn "Potentially sensitive files found matching pattern '$pattern':"
                $matches | ForEach-Object { Write-Host "  $_" }
                $foundSensitive = $true
            }
        }
        
        if ($foundSensitive -and -not $Force) {
            Write-Warn "Please review the above files to ensure no secrets are committed"
            $response = Read-Host "Continue anyway? (y/N)"
            if ($response -notmatch "^[Yy]$") {
                return $false
            }
        }
        
        Write-Info "Sensitive file check completed"
        return $true
    } catch {
        Write-Warn "Could not check for sensitive files: $_"
        return $true
    }
}

# Check commit message quality
function Test-CommitMessage {
    Write-Info "Checking commit message quality..."
    
    try {
        $commitMsg = (git log --format=%s -n 1 HEAD 2>$null).Trim()
        
        if ([string]::IsNullOrEmpty($commitMsg)) {
            Write-Warn "No commit message found"
            return $true
        }
        
        # Check minimum length
        if ($commitMsg.Length -lt 10) {
            Write-Error "Commit message too short: '$commitMsg'"
            Write-Error "Commit messages should be at least 10 characters"
            return $false
        }
        
        # Check for WIP/FIXME/TODO
        if ($commitMsg -match "(?i)(wip|fixme|todo|temp|temporary)") {
            Write-Warn "Commit message contains work-in-progress indicators: '$commitMsg'"
            Write-Warn "Consider cleaning up before pushing to shared branches"
        }
        
        Write-Info "Commit message check passed"
        return $true
    } catch {
        Write-Warn "Could not check commit message: $_"
        return $true
    }
}

# Check if branch is up to date
function Test-BranchFreshness {
    if (Test-ProtectedBranch) {
        return $true  # Skip for protected branches
    }
    
    Write-Info "Checking branch freshness..."
    
    try {
        # Try to fetch the latest changes
        git fetch origin main 2>$null | Out-Null
        git fetch origin master 2>$null | Out-Null
        
        # Find the main branch
        $mainBranch = ""
        foreach ($branch in @("main", "master")) {
            try {
                git show-ref --verify --quiet "refs/remotes/origin/$branch" 2>$null
                if ($LASTEXITCODE -eq 0) {
                    $mainBranch = $branch
                    break
                }
            } catch {
                continue
            }
        }
        
        if ([string]::IsNullOrEmpty($mainBranch)) {
            Write-Warn "Could not find main branch (main/master)"
            return $true
        }
        
        # Check if branch is behind
        $mergeBase = (git merge-base HEAD "origin/$mainBranch" 2>$null).Trim()
        $targetHead = (git rev-parse "origin/$mainBranch" 2>$null).Trim()
        
        if (![string]::IsNullOrEmpty($mergeBase) -and ![string]::IsNullOrEmpty($targetHead) -and $mergeBase -ne $targetHead) {
            $commitsBehind = (git rev-list --count "$mergeBase..$targetHead" 2>$null).Trim()
            Write-Warn "This branch is $commitsBehind commits behind origin/$mainBranch"
            Write-Warn "Consider updating your branch:"
            Write-Host "  git fetch origin"
            Write-Host "  git merge origin/$mainBranch"
            Write-Host ""
            
            if (-not $Force) {
                $response = Read-Host "Continue anyway? (y/N)"
                if ($response -notmatch "^[Yy]$") {
                    return $false
                }
            }
        } else {
            Write-Info "Branch is up to date with origin/$mainBranch"
        }
        
        return $true
    } catch {
        Write-Warn "Could not check branch freshness: $_"
        return $true
    }
}

# Main execution
function Main {
    Write-Info "Running branch protection pre-commit checks..."
    Write-Info "Current branch: $currentBranch"
    Write-Host ""
    
    $allPassed = $true
    
    # Run all checks
    if (-not (Test-DirectPushProtection)) { $allPassed = $false }
    if (-not (Test-LargeFiles)) { $allPassed = $false }
    if (-not (Test-SensitiveFiles)) { $allPassed = $false }
    if (-not (Test-CommitMessage)) { $allPassed = $false }
    if (-not (Test-BranchFreshness)) { $allPassed = $false }
    
    Write-Host ""
    if ($allPassed) {
        Write-Info "All branch protection checks passed! ✅"
        Write-Info "You can now push your changes safely."
        exit 0
    } else {
        Write-Error "Some branch protection checks failed! ❌"
        Write-Error "Please fix the issues above before pushing."
        exit 1
    }
}

# Run main function
Main