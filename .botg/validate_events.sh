#!/bin/bash
# validate_events.sh - Validate events.jsonl format

set -e

EVENTS_FILE="${1:-.botg/events.jsonl}"

if [ ! -f "$EVENTS_FILE" ]; then
    echo "Events file not found: $EVENTS_FILE"
    exit 1
fi

echo "Validating events in: $EVENTS_FILE"

VALID_COUNT=0
INVALID_COUNT=0
LINE_NUM=0

while IFS= read -r line; do
    LINE_NUM=$((LINE_NUM + 1))
    
    # Skip empty lines
    if [ -z "$line" ]; then
        continue
    fi
    
    # Validate JSON structure
    if ! echo "$line" | jq . > /dev/null 2>&1; then
        echo "Line $LINE_NUM: Invalid JSON"
        INVALID_COUNT=$((INVALID_COUNT + 1))
        continue
    fi
    
    # Check required fields
    MISSING_FIELDS=""
    for field in ts run_id pr stage level msg kv; do
        if ! echo "$line" | jq -e ".$field" > /dev/null 2>&1; then
            MISSING_FIELDS="$MISSING_FIELDS $field"
        fi
    done
    
    if [ -n "$MISSING_FIELDS" ]; then
        echo "Line $LINE_NUM: Missing fields:$MISSING_FIELDS"
        INVALID_COUNT=$((INVALID_COUNT + 1))
    else
        VALID_COUNT=$((VALID_COUNT + 1))
    fi
    
done < "$EVENTS_FILE"

echo "Validation complete:"
echo "  Valid events: $VALID_COUNT"
echo "  Invalid events: $INVALID_COUNT"

if [ "$INVALID_COUNT" -gt 0 ]; then
    exit 1
fi
