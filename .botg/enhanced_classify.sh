#!/bin/bash
# Enhanced classification with transient error detection
set -e

EXIT_CODE="${1:-1}"
mkdir -p .botg

echo "Classifying exit code: $EXIT_CODE"

# Transient error codes (network, timeout, etc)
TRANSIENT_CODES=(502 503 504 124 143)

STATUS="NEEDS_ACTION"
REASON="unknown_failure"

case "$EXIT_CODE" in
  0)
    STATUS="PASS"
    REASON="success"
    ;;
  502|503|504)
    STATUS="AUTO_RERUN"
    REASON="network_error"
    ;;
  124)
    STATUS="AUTO_RERUN"
    REASON="timeout"
    ;;
  143)
    STATUS="AUTO_RERUN"
    REASON="sigterm"
    ;;
  *)
    STATUS="NEEDS_ACTION"
    REASON="test_failure"
    ;;
esac

# Write classification result
cat > .botg/status.json << EOF
{
  "status": "$STATUS",
  "reason": "$REASON",
  "exit_code": $EXIT_CODE,
  "timestamp": "$(date -u +%FT%TZ)"
}
EOF

echo "Classification: $STATUS ($REASON)"
