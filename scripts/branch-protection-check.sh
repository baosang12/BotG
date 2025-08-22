#!/usr/bin/env bash
# Branch Protection Pre-commit Hook
# This script helps enforce branch protection rules locally before pushing

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo_info() { echo -e "${GREEN}[INFO]${NC} $*"; }
echo_warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
echo_error() { echo -e "${RED}[ERROR]${NC} $*"; }

# Get current branch
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
PROTECTED_BRANCHES=("main" "master" "develop")

# Check if we're on a protected branch
is_protected_branch() {
    for branch in "${PROTECTED_BRANCHES[@]}"; do
        if [[ "$CURRENT_BRANCH" == "$branch" ]]; then
            return 0
        fi
    done
    return 1
}

# Check if trying to push to protected branch
check_direct_push_protection() {
    if is_protected_branch; then
        echo_error "You are on protected branch '$CURRENT_BRANCH'"
        echo_error "Direct pushes to protected branches are not allowed!"
        echo_error "Please create a feature branch and use pull requests:"
        echo ""
        echo "  git checkout -b feature/your-feature-name"
        echo "  # make your changes"
        echo "  git add ."
        echo "  git commit -m 'Your commit message'"
        echo "  git push origin feature/your-feature-name"
        echo "  # then create a PR via GitHub web interface"
        echo ""
        return 1
    fi
    return 0
}

# Check for large files
check_large_files() {
    echo_info "Checking for large files (>5MB)..."
    
    # Check staged files
    LARGE_FILES=$(git diff --cached --name-only | xargs -I {} find {} -type f -size +5M 2>/dev/null || true)
    
    if [[ -n "$LARGE_FILES" ]]; then
        echo_error "Large files detected (>5MB):"
        echo "$LARGE_FILES"
        echo_error "Consider using Git LFS for large files or removing them"
        return 1
    fi
    
    echo_info "No large files detected"
    return 0
}

# Check for sensitive files
check_sensitive_files() {
    echo_info "Checking for potentially sensitive files..."
    
    SENSITIVE_PATTERNS=(
        "\.env$"
        "\.key$" 
        "\.pem$"
        "\.p12$"
        "\.pfx$"
        "password"
        "config\.json$"
        "secrets\."
    )
    
    FOUND_SENSITIVE=false
    for pattern in "${SENSITIVE_PATTERNS[@]}"; do
        FOUND=$(git diff --cached --name-only | grep -E "$pattern" || true)
        if [[ -n "$FOUND" ]]; then
            echo_warn "Potentially sensitive files found matching pattern '$pattern':"
            echo "$FOUND"
            FOUND_SENSITIVE=true
        fi
    done
    
    if [[ "$FOUND_SENSITIVE" == "true" ]]; then
        echo_warn "Please review the above files to ensure no secrets are committed"
        read -p "Continue anyway? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            return 1
        fi
    fi
    
    echo_info "Sensitive file check completed"
    return 0
}

# Check commit message quality
check_commit_message() {
    echo_info "Checking commit message quality..."
    
    # Get the last commit message
    COMMIT_MSG=$(git log --format=%s -n 1 HEAD 2>/dev/null || echo "")
    
    if [[ -z "$COMMIT_MSG" ]]; then
        echo_warn "No commit message found"
        return 0
    fi
    
    # Check minimum length
    if [[ ${#COMMIT_MSG} -lt 10 ]]; then
        echo_error "Commit message too short: '$COMMIT_MSG'"
        echo_error "Commit messages should be at least 10 characters"
        return 1
    fi
    
    # Check for WIP/FIXME/TODO
    if echo "$COMMIT_MSG" | grep -qiE "(wip|fixme|todo|temp|temporary)"; then
        echo_warn "Commit message contains work-in-progress indicators: '$COMMIT_MSG'"
        echo_warn "Consider cleaning up before pushing to shared branches"
    fi
    
    echo_info "Commit message check passed"
    return 0
}

# Check if branch is up to date (if not on protected branch)
check_branch_freshness() {
    if is_protected_branch; then
        return 0  # Skip for protected branches
    fi
    
    echo_info "Checking branch freshness..."
    
    # Try to fetch the latest changes
    git fetch origin main 2>/dev/null || git fetch origin master 2>/dev/null || true
    
    # Find the main branch
    MAIN_BRANCH=""
    for branch in "main" "master"; do
        if git show-ref --verify --quiet "refs/remotes/origin/$branch"; then
            MAIN_BRANCH="$branch"
            break
        fi
    done
    
    if [[ -z "$MAIN_BRANCH" ]]; then
        echo_warn "Could not find main branch (main/master)"
        return 0
    fi
    
    # Check if branch is behind
    MERGE_BASE=$(git merge-base HEAD "origin/$MAIN_BRANCH" 2>/dev/null || echo "")
    TARGET_HEAD=$(git rev-parse "origin/$MAIN_BRANCH" 2>/dev/null || echo "")
    
    if [[ -n "$MERGE_BASE" && -n "$TARGET_HEAD" && "$MERGE_BASE" != "$TARGET_HEAD" ]]; then
        COMMITS_BEHIND=$(git rev-list --count "$MERGE_BASE..$TARGET_HEAD" 2>/dev/null || echo "unknown")
        echo_warn "This branch is $COMMITS_BEHIND commits behind origin/$MAIN_BRANCH"
        echo_warn "Consider updating your branch:"
        echo "  git fetch origin"
        echo "  git merge origin/$MAIN_BRANCH"
        echo ""
        read -p "Continue anyway? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            return 1
        fi
    else
        echo_info "Branch is up to date with origin/$MAIN_BRANCH"
    fi
    
    return 0
}

# Run all checks
main() {
    echo_info "Running branch protection pre-commit checks..."
    echo_info "Current branch: $CURRENT_BRANCH"
    echo ""
    
    # Run all checks
    check_direct_push_protection || exit 1
    check_large_files || exit 1
    check_sensitive_files || exit 1
    check_commit_message || exit 1
    check_branch_freshness || exit 1
    
    echo ""
    echo_info "All branch protection checks passed! ✅"
    echo_info "You can now push your changes safely."
}

# Run main function
main "$@"