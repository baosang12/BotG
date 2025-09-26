#!/bin/bash
# enhanced_classify.sh - Enhanced classification with heartbeat/artifact checks

set -e

CODE="${1:-0}"
HAS_HEARTBEAT="${HAS_HEARTBEAT:-true}"
HAS_ARTIFACTS="${HAS_ARTIFACTS:-true}"
HEARTBEAT_AGE_S="${HEARTBEAT_AGE_S:-0}"

STATUS="PASS"
REASON=""

# Check test exit code first
if [ "$CODE" != "0" ]; then
    # Check for transient/infrastructure errors
    if grep -Ei '5\d{2}|timeout|oom|rate.?limit|connection.*reset|network.*unreachable' /home/runner/work/_temp/* 2>/dev/null; then
        STATUS="AUTO_RERUN"
        REASON="transient_error"
    else
        STATUS="NEEDS_ACTION" 
        REASON="tests_failed"
    fi
# Check heartbeat and artifacts for PASS cases
elif [ "$HAS_HEARTBEAT" != "true" ] || [ "$HAS_ARTIFACTS" != "true" ]; then
    STATUS="NEEDS_ACTION"
    if [ "$HAS_HEARTBEAT" != "true" ]; then
        REASON="missing_heartbeat"
    else
        REASON="missing_artifacts"
    fi
elif [ "$HEARTBEAT_AGE_S" -gt 300 ]; then
    STATUS="NEEDS_ACTION"
    REASON="stale_heartbeat"
fi

# Output results
jq -n --arg s "$STATUS" --arg r "$REASON" --arg age "$HEARTBEAT_AGE_S" \
  '{status:$s, reason:$r, heartbeat_age_s:($age|tonumber), ts:"'"$(date -u +%FT%TZ)"'"}' > .botg/status.json

echo "Classification: $STATUS ($REASON)"
cat .botg/status.json
