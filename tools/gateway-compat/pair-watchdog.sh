#!/bin/bash
# Auto-approve pending device pair requests. Runs in a tight loop until
# killed, polling `openclaw devices list --json` every 2s and approving
# every requestId it finds. Spawned by setup-gateway.sh as a setsid'd
# background process so the test harness doesn't have to deal with the
# tray's autopair vs. gateway request-registration race (which Plan A
# spent 8 iterations trying to work around).
set -uo pipefail

OPENCLAW_BIN="/opt/openclaw/bin/openclaw"
WATCHDOG_LOG="/var/openclaw-test/pair-watchdog.log"
mkdir -p "$(dirname "$WATCHDOG_LOG")"

ts() { date -u +'%Y-%m-%dT%H:%M:%SZ'; }
log() { echo "[$(ts)] $*" >> "$WATCHDOG_LOG"; }

log "watchdog started (pid $$)"
while true; do
  list_json=$("$OPENCLAW_BIN" devices list --json 2>/dev/null || true)
  if [ -n "$list_json" ]; then
    # Extract requestIds from the "pending": [...] array via grep/sed.
    # Avoids a python3/jq dependency at the cost of a tiny regex.
    request_ids=$(echo "$list_json" | grep -oE '"requestId"[[:space:]]*:[[:space:]]*"[^"]+"' | sed -E 's/.*"requestId"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
    for rid in $request_ids; do
      log "approving $rid"
      out=$("$OPENCLAW_BIN" devices approve "$rid" 2>&1 || true)
      log "approve result: ${out//$'\n'/ }"
    done
  fi
  sleep 2
done
