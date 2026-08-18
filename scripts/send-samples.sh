#!/usr/bin/env bash
# Posts every sample message to the running server (docker compose up) and prints the HTTP status and ACK.
# Usage: scripts/send-samples.sh [BASE_URL]      (default http://localhost:8080)
#        scripts/send-samples.sh --json          (ask for the JSON summary instead of the HL7 ACK)
set -euo pipefail

BASE_URL="http://localhost:8080"
ACCEPT="text/plain"
for arg in "$@"; do
  case "$arg" in
    --json) ACCEPT="application/json" ;;
    *) BASE_URL="$arg" ;;
  esac
done

DIR="$(cd "$(dirname "$0")/.." && pwd)"

for f in "$DIR"/samples/*.hl7; do
  name="$(basename "$f")"
  printf '\n=== %s ===\n' "$name"
  # The ACK uses \r segment terminators; convert to newlines so it displays on separate lines.
  curl -sS -X POST "$BASE_URL/messages" \
       -H "Content-Type: text/plain" \
       -H "Accept: $ACCEPT" \
       --data-binary @"$f" \
       -w '\n[HTTP %{http_code}]\n' | tr '\r' '\n'
done
