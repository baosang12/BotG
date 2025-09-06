#!/usr/bin/env bash
# Setup Branch Protection Pre-commit Hook

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOOKS_DIR="$REPO_ROOT/.git/hooks"
PROTECTION_SCRIPT="$REPO_ROOT/scripts/branch-protection-check.sh"

echo "Setting up branch protection pre-commit hook..."

# Ensure hooks directory exists
mkdir -p "$HOOKS_DIR"

# Create pre-commit hook
cat > "$HOOKS_DIR/pre-commit" << 'EOF'
#!/usr/bin/env bash
# Pre-commit hook to run branch protection checks

REPO_ROOT="$(git rev-parse --show-toplevel)"
PROTECTION_SCRIPT="$REPO_ROOT/scripts/branch-protection-check.sh"

if [[ -f "$PROTECTION_SCRIPT" ]]; then
    echo "Running branch protection checks..."
    if ! "$PROTECTION_SCRIPT"; then
        echo "Branch protection checks failed!"
        exit 1
    fi
else
    echo "Warning: Branch protection script not found at $PROTECTION_SCRIPT"
fi
EOF

# Make hook executable
chmod +x "$HOOKS_DIR/pre-commit"

echo "✅ Branch protection pre-commit hook installed!"
echo "The hook will run automatically before each commit."
echo ""
echo "To bypass the hook temporarily (not recommended):"
echo "  git commit --no-verify -m 'Your message'"
echo ""
echo "To run the check manually:"
echo "  ./scripts/branch-protection-check.sh"
echo ""
echo "To uninstall the hook:"
echo "  rm .git/hooks/pre-commit"