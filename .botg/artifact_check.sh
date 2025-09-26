#!/bin/bash
# artifact_check.sh - Check if required artifacts exist

set -e

REQUIRED_ARTIFACTS=("build.log" "test_results.xml" "coverage.json")
HAS_ALL_ARTIFACTS=true

for artifact in "${REQUIRED_ARTIFACTS[@]}"; do
    if [ ! -f ".botg/$artifact" ]; then
        echo "Missing artifact: $artifact"
        HAS_ALL_ARTIFACTS=false
    fi
done

if [ "$HAS_ALL_ARTIFACTS" = true ]; then
    echo "HAS_ARTIFACTS=true" >> $GITHUB_ENV
else
    echo "HAS_ARTIFACTS=false" >> $GITHUB_ENV
fi

echo "All required artifacts present: $HAS_ALL_ARTIFACTS"
