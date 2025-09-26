#!/bin/bash
# Artifact check: validate critical artifacts exist
set -e

echo "Checking critical artifacts..."

# Check for config files
CONFIGS=("config.runtime.json" "BotG.sln")
for cfg in "${CONFIGS[@]}"; do
  if [ ! -f "$cfg" ]; then
    echo " Missing config: $cfg"
    exit 1
  fi
  echo " Found: $cfg"
done

# Check executable exists
if [ ! -f "BotG/BotG.cs" ]; then
  echo " Missing main source: BotG/BotG.cs"
  exit 1
fi
echo " Found: BotG/BotG.cs"

echo "All artifacts validated"
