ci: fix GitHub Actions by installing .NET 6/9, pin ubuntu-22.04, and add diagnostics

Why
- CI failed on ubuntu-latest due to missing .NET 6 runtime for tests.

Changes
- Install .NET SDK/runtimes 6.0.x and 9.0.x via actions/setup-dotnet.
- Pin Linux runner to ubuntu-22.04 for stable .NET 6 support.
- Add `dotnet --info` / `dotnet --list-runtimes` diagnostics step.
- Make tests fail properly (remove `|| true`).

Impact
- Workflow-only change. No product code modified.

Verification
- "Show installed runtimes" step should list `Microsoft.NETCore.App 6.0.x`.
- Test step should run without runtime-missing error.

Post-merge
- Keep/enable branch protection rule "Require CI Success" for main.
