#!/bin/bash
# Heartbeat check: fail if last heartbeat > 5min
set -e

EVENTS_FILE=".botg/events.jsonl"
THRESHOLD=300  # 5min

if [ ! -f "$EVENTS_FILE" ]; then
  echo "No events file found"
  exit 0
fi

# Get last heartbeat timestamp
LAST_HB=$(grep '"msg":"heartbeat"' "$EVENTS_FILE" | tail -1 | jq -r .ts 2>/dev/null || echo "")
if [ -z "$LAST_HB" ]; then
  echo "No heartbeat events found"
  exit 0
fi

# Convert to epoch and check age
LAST_EPOCH=$(date -d "$LAST_HB" +%s 2>/dev/null || echo "0")
NOW_EPOCH=$(date +%s)
AGE=$((NOW_EPOCH - LAST_EPOCH))

echo "Last heartbeat: $LAST_HB (${AGE}s ago)"
if [ "$AGE" -gt "$THRESHOLD" ]; then
  echo " Heartbeat stale (${AGE}s > ${THRESHOLD}s)"
  exit 1
fi

echo " Heartbeat fresh"
