# Branch Protection Rules

This repository implements branch protection rules to maintain code quality and prevent accidental changes to critical branches.

## Protected Branches

The following branches are protected:
- `main` - Primary production branch
- `master` - Alternative primary branch (if used)
- `develop` - Development integration branch

## Protection Rules

### 1. No Direct Pushes
- Direct pushes to protected branches are **not allowed**
- All changes must go through Pull Requests
- The `branch-protection.yml` workflow enforces this rule

### 2. Required Pull Request Reviews
- Pull requests must have a meaningful title (not just the branch name)
- Pull requests must include a description
- Changes should be reviewed before merging

### 3. Required Status Checks
The following checks must pass before merging:
- **Build Success**: `dotnet build` must complete without errors
- **Test Success**: `dotnet test` must pass all tests  
- **Code Quality**: No large files, sensitive files check
- **Commit Quality**: Meaningful commit messages

### 4. Branch Freshness
- Branches should be reasonably up-to-date with the target branch
- The workflow warns if branches are behind the target
- Consider updating your branch before merging:
  ```bash
  git fetch origin
  git merge origin/main
  ```

### 5. Code Quality Standards
- No files larger than 5MB (use Git LFS if needed)
- No obviously sensitive files (keys, passwords, etc.)
- Commit messages must be at least 10 characters
- Avoid WIP/TODO/FIXME in final commit messages

## How to Contribute

1. **Create a feature branch**:
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Make your changes** and commit with meaningful messages:
   ```bash
   git add .
   git commit -m "Add feature X to improve Y"
   ```

3. **Push your branch**:
   ```bash
   git push origin feature/your-feature-name
   ```

4. **Create a Pull Request** via GitHub web interface:
   - Use a descriptive title
   - Include a detailed description
   - Wait for CI checks to pass
   - Request reviews if needed

5. **Merge after approval** and passing checks

## Workflow Files

- `.github/workflows/branch-protection.yml` - Enforces protection rules
- `.github/workflows/ci.yml` - Builds and tests the code

## Emergency Procedures

If you need to bypass protection rules in an emergency:

1. **Contact repository administrators** for temporary access
2. **Document the emergency** in the commit message
3. **Create a follow-up PR** to address any issues properly

## Configuration

To modify branch protection rules:

1. Edit `.github/workflows/branch-protection.yml`
2. Update this documentation
3. Test changes on a feature branch first
4. Get approval for protection rule changes

## Repository Admin Setup

For repository administrators to enable GitHub native branch protection:

1. Go to Settings → Branches
2. Add rule for `main` branch:
   - ✅ Require a pull request before merging
   - ✅ Require status checks to pass before merging
   - Add required status checks: `Enforce Branch Protection Rules`, `Require CI Success`
   - ✅ Require branches to be up to date before merging
   - ✅ Require conversation resolution before merging
   - ✅ Do not allow bypassing the above settings

This provides defense-in-depth with both workflow enforcement and GitHub native protection.