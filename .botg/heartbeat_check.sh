#!/bin/bash
# heartbeat_check.sh - Check if recent heartbeat exists

set -e

HEARTBEAT_THRESHOLD_SECONDS=300  # 5 minutes
HEARTBEAT_FILE=".botg/heartbeat.txt"

if [ ! -f "$HEARTBEAT_FILE" ]; then
    echo "HAS_HEARTBEAT=false" >> $GITHUB_ENV
    echo "HEARTBEAT_AGE_S=999" >> $GITHUB_ENV
    exit 0
fi

LAST_HEARTBEAT=$(stat -f %m "$HEARTBEAT_FILE" 2>/dev/null || stat -c %Y "$HEARTBEAT_FILE" 2>/dev/null || echo 0)
CURRENT_TIME=$(date +%s)
AGE_SECONDS=$((CURRENT_TIME - LAST_HEARTBEAT))

if [ "$AGE_SECONDS" -gt "$HEARTBEAT_THRESHOLD_SECONDS" ]; then
    echo "HAS_HEARTBEAT=false" >> $GITHUB_ENV
    echo "HEARTBEAT_AGE_S=$AGE_SECONDS" >> $GITHUB_ENV
else
    echo "HAS_HEARTBEAT=true" >> $GITHUB_ENV
    echo "HEARTBEAT_AGE_S=$AGE_SECONDS" >> $GITHUB_ENV
fi

echo "Heartbeat age: ${AGE_SECONDS}s (threshold: ${HEARTBEAT_THRESHOLD_SECONDS}s)"
