# Workflows notes

- Branch protection requires a check named exactly `CI - build & test`.
- The main CI workflow runs `build-test` and a `quick-smoke` job.
- To satisfy the required context even when jobs fan out, we publish an aggregated commit status from job `report-required-status` with the exact name.
- If you rename the workflow or jobs, keep the status context stable or update branch protection accordingly.