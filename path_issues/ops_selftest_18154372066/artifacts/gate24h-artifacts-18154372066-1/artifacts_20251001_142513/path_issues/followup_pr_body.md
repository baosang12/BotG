Title: ci: validate reconstructed trades from smoke artifacts (postrun hardening)

Summary
- Adds a CI job “Reconstruct & Validate (from smoke)” that downloads the 60s Quick Smoke artifacts, runs a deterministic reconstruction from orders.csv, and enforces two gates:
  - close_time >= open_time for all reconstructed trades
  - PnL values are Decimal-formatted with 1–8 fractional digits (no float artifacts)
- Publishes recon-validation-artifacts for debugging (CSV + JSON summary).
- Keeps the aggregated required status “CI - build & test” green only if build-test, quick-smoke, and reconstruct-validate all pass.

What changed
- .github/workflows/ci.yml: add reconstruct-validate job and include it in the aggregated required status.
- scripts/ci_reconstruct_validate.ps1: helper used by CI to run reconstruction+validation on the downloaded smoke artifacts.

Why
- Review feedback: floating-point PnL artifacts and occasional close_time < open_time in reconstructed trades. We already fixed the reconstructor to use Decimal and to clamp time ordering; this PR guards those invariants in CI.

Acceptance checklist
- [ ] Build and Test (windows-latest) passes.
- [ ] Quick Smoke (60s) passes with orphan_after == 0.
- [ ] Reconstruct & Validate (from smoke) passes with:
      - badTimeCount == 0
      - badPnlCount == 0
- [ ] recon-validation-artifacts uploaded and contains reconstruct_validation_*.json

Notes
- This job is Windows-only, runs after Quick Smoke, and typically finishes in ~10–20s.
- If it fails, open recon-validation-artifacts in the run and inspect …_details.csv for offending rows.
