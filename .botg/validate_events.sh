#!/bin/bash
# Validate events.jsonl against schema
set -e

EVENTS_FILE="${1:-.botg/events.jsonl}"

if [ ! -f "$EVENTS_FILE" ]; then
  echo " Events file not found: $EVENTS_FILE"
  exit 1
fi

echo "Validating events file: $EVENTS_FILE"

# Count events and validate basic structure
COUNT=0
while IFS= read -r line; do
  COUNT=$((COUNT + 1))
  # Basic JSON validation
  echo "$line" | jq . >/dev/null || {
    echo " Invalid JSON at line $COUNT"
    exit 1
  }
  
  # Check required fields
  for field in ts run_id pr stage level msg; do
    echo "$line" | jq -e ".$field" >/dev/null || {
      echo " Missing field '$field' at line $COUNT"
      exit 1
    }
  done
done < "$EVENTS_FILE"

echo " Validated $COUNT events"
echo "Schema: ts|run_id|pr|stage|level|msg|kv"
