#!/usr/bin/env bash
# Posts every sample message to the running server (docker compose up), prints the HTTP status + ACK for each,
# then (after the background worker has run) fetches each message's verdict from GET /messages/{id}.
# Usage: scripts/send-samples.sh [BASE_URL]      (default http://localhost:8080)
#        scripts/send-samples.sh --json          (ask for the JSON receipt instead of the HL7 ACK)
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
ids=()

for f in "$DIR"/samples/*.hl7; do
  name="$(basename "$f")"
  printf '\n=== %s ===\n' "$name"
  # -D - dumps response headers (to pick up X-Message-Id). Headers end in \r\n (strip the \r); the ACK body uses
  # bare \r segment terminators (turn into newlines for display).
  out="$(curl -sS -D - -X POST "$BASE_URL/messages" \
           -H "Content-Type: text/plain" \
           -H "Accept: $ACCEPT" \
           --data-binary @"$f" \
           -w '\n[HTTP %{http_code}]' | sed 's/\r$//' | tr '\r' '\n')"
  id="$(printf '%s\n' "$out" | awk 'tolower($1)=="x-message-id:"{print $2}')"
  # body = everything after the blank line that ends the headers
  printf '%s\n' "$out" | awk 'body{print} /^$/{body=1}'
  ids+=("$id")
done

# Give the worker a moment (it normally finishes in milliseconds).
sleep 1

printf '\n=== Verdicts (GET %s/messages/{id}) ===\n' "$BASE_URL"
for id in "${ids[@]}"; do
  [ -n "$id" ] || continue
  summary="$(curl -sS "$BASE_URL/messages/$id" \
    | grep -oE '"status":"[a-z]+"|"code":"[A-Z_]+"|"duplicateOf":[0-9]+|"messageControlId":(null|"[^"]*")|"accessionNumber":"[^"]*"' \
    | tr '\n' ' ')"
  printf 'id=%s  %s\n' "$id" "$summary"
done
echo
echo "Details: curl $BASE_URL/messages/<id>    quarantine: curl '$BASE_URL/messages?status=rejected'    DB: scripts/show-db.sh"
