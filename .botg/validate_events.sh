#!/bin/bash

set -e

EVENTS_FILE="${1:-.botg/events.jsonl}"

if [ ! -f "$EVENTS_FILE" ]; then
