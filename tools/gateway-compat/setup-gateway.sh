#!/bin/bash
# Stand up a known-good openclaw gateway inside the WSL distro for the
# gateway-compat test harness. Runs as the 'openclaw' user.
#
# Idempotent: re-running on a host where the gateway is already up is a fast
# no-op except for re-applying the fake-LLM provider patch (which the openclaw
# config CLI handles idempotently).
#
# Inputs:
#   OPENCLAW_GATEWAY_VERSION  npm version or dist-tag (default: "latest")
#   FAKE_LLM_PORT             local port for the fake-LLM mock (default: 18888)
#   GATEWAY_PORT              local port the gateway binds to (default: 18789)
#   SETUP_CODE_OUT            where to write the bootstrap setup-code JSON
#                             (default: /var/openclaw-test/setup-code.json)
#   REPO_WSL_PATH             /mnt/... path to repo root in WSL (required for fake-LLM)
#
# Side effects:
#   - npm-installs openclaw under /opt/openclaw (uses the official install-cli.sh)
#   - spawns the gateway via `nohup openclaw gateway --port <port>` (NOT
#     `openclaw gateway start`, which returns exit 0 but the spawned node
#     process dies within seconds - learned the hard way in CI iteration 6)
#   - applies the fake-LLM provider patch via `openclaw config patch`
#   - launches the fake-LLM mock (tools/fake-llm-server/server.mjs)
#   - writes a bootstrap setup-code JSON to $SETUP_CODE_OUT
set -euo pipefail

: "${REPO_WSL_PATH:?REPO_WSL_PATH must be set}"
OPENCLAW_GATEWAY_VERSION="${OPENCLAW_GATEWAY_VERSION:-latest}"
FAKE_LLM_PORT="${FAKE_LLM_PORT:-28888}"
GATEWAY_PORT="${GATEWAY_PORT:-28789}"
SETUP_CODE_OUT="${SETUP_CODE_OUT:-/var/openclaw-test/setup-code.json}"

OPENCLAW_BIN="/opt/openclaw/bin/openclaw"
GATEWAY_LOG="/var/openclaw-test/openclaw-gateway.log"

mkdir -p "$(dirname "$SETUP_CODE_OUT")" "$(dirname "$GATEWAY_LOG")"

#----------------------------------------------------------------------
# Step 1: Install openclaw if missing or version mismatch
#----------------------------------------------------------------------
install_openclaw() {
  echo "[setup-gateway] Installing openclaw@${OPENCLAW_GATEWAY_VERSION}..." >&2
  # The official install-cli.sh respects OPENCLAW_PREFIX, OPENCLAW_INSTALL_METHOD,
  # and OPENCLAW_VERSION. Pass --no-onboard so it doesn't try to launch the TUI.
  # SHARP_IGNORE_GLOBAL_LIBVIPS=1 avoids a noisy build step for an optional dep.
  local cmd
  cmd="curl -fsSL --proto '=https' --tlsv1.2 'https://openclaw.ai/install-cli.sh' | "
  cmd+="OPENCLAW_PREFIX='/opt/openclaw' OPENCLAW_INSTALL_METHOD='npm' "
  cmd+="OPENCLAW_VERSION='${OPENCLAW_GATEWAY_VERSION}' SHARP_IGNORE_GLOBAL_LIBVIPS=1 "
  cmd+="bash -s -- --json --prefix '/opt/openclaw' --version '${OPENCLAW_GATEWAY_VERSION}' --no-onboard"
  bash -c "$cmd"
}

needs_install=true
if [ -x "$OPENCLAW_BIN" ]; then
  # If the desired version is "latest", always reinstall to pick up any new
  # publish; otherwise skip the install when the on-disk version matches.
  if [ "$OPENCLAW_GATEWAY_VERSION" != "latest" ]; then
    installed_version="$("$OPENCLAW_BIN" --version 2>/dev/null | head -n1 | tr -d '[:space:]' || true)"
    if [ "$installed_version" = "$OPENCLAW_GATEWAY_VERSION" ]; then
      needs_install=false
    fi
  fi
fi

if [ "$needs_install" = "true" ]; then
  install_openclaw
else
  echo "[setup-gateway] openclaw ${OPENCLAW_GATEWAY_VERSION} already installed, skipping install." >&2
fi

"$OPENCLAW_BIN" --version

#----------------------------------------------------------------------
# Step 2: Run `openclaw gateway install` (creates user-systemd unit + config).
# Idempotent - --force overwrites any prior install.
#
# Then IMMEDIATELY tear down the user-systemd unit. Its Restart=always
# fights our nohup-spawn (PID-race on port 18789): the unit reads the
# *pre-patch* openclaw.json, gets a stale auth.token in memory, wins the
# port race, and then the tray/CLI's post-patch token never matches. We
# manage gateway lifecycle ourselves via nohup.
#----------------------------------------------------------------------
"$OPENCLAW_BIN" gateway install --force --port "$GATEWAY_PORT" || \
  echo "[setup-gateway] gateway install returned non-zero; ignoring (we run nohup)." >&2

# Disable the systemd unit that gateway-install registers — its
# Restart=always races our nohup-spawn (PID-race on the gateway port).
if [ -z "${XDG_RUNTIME_DIR:-}" ]; then
  export XDG_RUNTIME_DIR="/run/user/$(id -u)"
fi
systemctl --user stop    openclaw-gateway.service 2>/dev/null || true
systemctl --user disable openclaw-gateway.service 2>/dev/null || true
systemctl --user reset-failed openclaw-gateway.service 2>/dev/null || true
for _ in $(seq 1 10); do
  if ! ss -tlnp 2>/dev/null | grep -q ":${GATEWAY_PORT}\b"; then break; fi
  sleep 1
done

#----------------------------------------------------------------------
# Step 2.5: Patch gateway.port so the running gateway actually binds the
# port we asked for. The `openclaw gateway --port N` CLI flag is only
# honored by some code paths; the http server bind path reads
# gateway.port from openclaw.json (defaults to 18789, the production
# value, which collides with a user's real OpenClawGateway distro under
# WSL2 mirrored-mode networking that shares localhost across distros).
#----------------------------------------------------------------------
PORT_PATCH_JSON='{"gateway":{"port":'"${GATEWAY_PORT}"'}}'
printf '%s' "$PORT_PATCH_JSON" | "$OPENCLAW_BIN" config patch --stdin || \
  { echo "[setup-gateway] failed to patch gateway.port" >&2; exit 1; }

#----------------------------------------------------------------------
# Step 3: Spawn the gateway via nohup (NOT `openclaw gateway start`).
#
# Plan A learned the hard way that `openclaw gateway start` returns exit 0
# but the spawned node process dies within seconds with no Restart=on-failure
# unit registered. Bypass the broken start command entirely.
#
# Guarded by pgrep so re-running is a no-op.
#----------------------------------------------------------------------
if ! pgrep -f "openclaw/dist/index.js gateway" >/dev/null 2>&1; then
  echo "[setup-gateway] Spawning openclaw gateway via setsid+nohup on port ${GATEWAY_PORT}..." >&2
  # setsid detaches into a new session so the process survives the
  # parent wsl.exe invocation exiting. nohup alone is not enough in
  # WSL2 mirrored mode - WSL terminates the user session's process
  # tree when the wsl.exe invocation that started it returns.
  setsid -f nohup "$OPENCLAW_BIN" gateway --port "$GATEWAY_PORT" \
    >> "$GATEWAY_LOG" 2>&1 < /dev/null
fi

# Wait for the gateway port to bind (up to 30 s).
for _ in $(seq 1 30); do
  if ss -tlnp 2>/dev/null | grep -q ":${GATEWAY_PORT}\b"; then
    break
  fi
  sleep 1
done
if ! ss -tlnp 2>/dev/null | grep -q ":${GATEWAY_PORT}\b"; then
  echo "[setup-gateway] gateway did not bind ${GATEWAY_PORT} within 30s. Last 50 log lines:" >&2
  tail -n 50 "$GATEWAY_LOG" >&2 || true
  exit 1
fi
echo "[setup-gateway] gateway listening on ${GATEWAY_PORT}." >&2

#----------------------------------------------------------------------
# Step 4: Bring up the fake-LLM mock (Node.js server.mjs from
# tools/fake-llm-server/). Idempotent via pgrep.
#----------------------------------------------------------------------
if ! pgrep -f "fake-llm/server.mjs" >/dev/null 2>&1; then
  echo "[setup-gateway] Starting fake-LLM mock on ${FAKE_LLM_PORT}..." >&2
  mkdir -p /home/openclaw/fake-llm
  cp "${REPO_WSL_PATH}/tools/fake-llm-server/server.mjs" /home/openclaw/fake-llm/server.mjs
  setsid -f nohup env FAKE_LLM_PORT="$FAKE_LLM_PORT" FAKE_LLM_BIND=127.0.0.1 \
    node /home/openclaw/fake-llm/server.mjs \
    > /home/openclaw/fake-llm/server.log 2>&1 < /dev/null
  for _ in $(seq 1 15); do
    if curl -fsS "http://127.0.0.1:$FAKE_LLM_PORT/" >/dev/null 2>&1; then break; fi
    sleep 1
  done
fi

#----------------------------------------------------------------------
# Step 5: Patch the gateway config to point at fake-LLM.
#
# `openclaw config patch` is read-modify-write: retry up to 3 times on
# ConfigMutationConflictError. Schema requires models[] entries to have BOTH
# id and name (both min length 1); reasoning/input/cost/contextWindow/
# maxTokens are required by the strict schema too. Patch payload is strict
# JSON, NOT JSON5.
#----------------------------------------------------------------------
read -r -d '' PATCH_JSON <<EOF || true
{
  "models": {
    "providers": {
      "fake": {
        "api": "openai-completions",
        "baseUrl": "http://127.0.0.1:${FAKE_LLM_PORT}/v1",
        "apiKey": "test",
        "auth": "api-key",
        "models": [
          {
            "id": "fake-llm",
            "name": "fake-llm",
            "reasoning": false,
            "input": ["text"],
            "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 },
            "contextWindow": 200000,
            "maxTokens": 4096
          }
        ]
      }
    }
  },
  "agents": {
    "defaults": { "model": { "primary": "fake/fake-llm" } }
  }
}
EOF
patch_attempts=0
while true; do
  patch_attempts=$((patch_attempts + 1))
  if printf '%s' "$PATCH_JSON" | "$OPENCLAW_BIN" config patch --stdin 2>/tmp/patch.stderr; then
    break
  fi
  if [ "$patch_attempts" -ge 3 ]; then
    echo "[setup-gateway] config patch failed after ${patch_attempts} attempts. stderr:" >&2
    cat /tmp/patch.stderr >&2
    exit 1
  fi
  if grep -q ConfigMutationConflictError /tmp/patch.stderr; then
    echo "[setup-gateway] ConfigMutationConflictError, retrying patch (attempt ${patch_attempts})..." >&2
    sleep 1
    continue
  fi
  echo "[setup-gateway] config patch failed with unexpected error:" >&2
  cat /tmp/patch.stderr >&2
  exit 1
done

"$OPENCLAW_BIN" config validate

#----------------------------------------------------------------------
# Step 5a: Mirror gateway.auth.token into gateway.remote.token so the
# local CLI (and the test fixture's pre-seeded gateways.json) can talk
# to this gateway. Without this, even `openclaw devices list` from
# inside the distro returns:
#   1008: unauthorized: gateway token mismatch
#         (set gateway.remote.token to match gateway.auth.token)
# The CLI reads gateway.remote.token to know what to send; the server
# reads gateway.auth.token to know what to expect.
#----------------------------------------------------------------------
AUTH_TOKEN="$(grep -o '"token"[[:space:]]*:[[:space:]]*"[^"]*"' /home/openclaw/.openclaw/openclaw.json | head -n1 | sed 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
if [ -z "$AUTH_TOKEN" ]; then
  echo "[setup-gateway] could not extract gateway.auth.token from openclaw.json" >&2
  exit 1
fi
REMOTE_PATCH_JSON='{"gateway":{"remote":{"url":"ws://localhost:'"${GATEWAY_PORT}"'","token":"'"${AUTH_TOKEN}"'"}}}'
patch_attempts=0
while true; do
  patch_attempts=$((patch_attempts + 1))
  if printf '%s' "$REMOTE_PATCH_JSON" | "$OPENCLAW_BIN" config patch --stdin 2>/tmp/patch.stderr; then
    break
  fi
  if [ "$patch_attempts" -ge 3 ]; then
    echo "[setup-gateway] gateway.remote patch failed after ${patch_attempts} attempts:" >&2
    cat /tmp/patch.stderr >&2
    exit 1
  fi
  sleep 1
done

#----------------------------------------------------------------------
# Step 5b: Restart the gateway so the patch is live.
#
# The config patch CLI prints "Restart the gateway to apply." — patches
# only become effective on next gateway boot. Kill the nohup'd process
# we spawned in Step 3 and respawn.
#----------------------------------------------------------------------
pkill -f "openclaw/dist/index.js gateway" >/dev/null 2>&1 || true
# Give the kernel a moment to release the gateway port.
for _ in $(seq 1 10); do
  if ! ss -tlnp 2>/dev/null | grep -q ":${GATEWAY_PORT}\b"; then
    break
  fi
  sleep 1
done

echo "[setup-gateway] Respawning gateway after config patch..." >&2
setsid -f nohup "$OPENCLAW_BIN" gateway --port "$GATEWAY_PORT" \
  >> "$GATEWAY_LOG" 2>&1 < /dev/null

for _ in $(seq 1 30); do
  if ss -tlnp 2>/dev/null | grep -q ":${GATEWAY_PORT}\b"; then
    break
  fi
  sleep 1
done
if ! ss -tlnp 2>/dev/null | grep -q ":${GATEWAY_PORT}\b"; then
  echo "[setup-gateway] gateway did not re-bind ${GATEWAY_PORT} within 30s after patch restart. Last 50 log lines:" >&2
  tail -n 50 "$GATEWAY_LOG" >&2 || true
  exit 1
fi
echo "[setup-gateway] gateway respawned on ${GATEWAY_PORT}." >&2

#----------------------------------------------------------------------
# Step 5c: Start the pair-request watchdog. It auto-approves any tray
# device that tries to pair, sidestepping the autopair vs. request-
# registration race that Plan A spent eight iterations on.
#----------------------------------------------------------------------
if ! pgrep -f "pair-watchdog.sh" >/dev/null 2>&1; then
  WATCHDOG_SH="$(dirname "$0")/pair-watchdog.sh"
  echo "[setup-gateway] Starting pair watchdog..." >&2
  setsid -f nohup bash "$WATCHDOG_SH" \
    > /var/openclaw-test/pair-watchdog.out 2>&1 < /dev/null
fi

#----------------------------------------------------------------------
# Step 6: Emit a bootstrap-token setup-code. The tray uses this for
# first-time pairing - the gateway auto-approves the first device that
# presents the bootstrap token, no out-of-band approval needed (which
# would require the tray to retry connect on a pair-resolved event,
# which it currently does not do).
#----------------------------------------------------------------------
QR_JSON="$("$OPENCLAW_BIN" qr --json --url "ws://localhost:${GATEWAY_PORT}" 2>&1)"
BOOTSTRAP_B64="$(printf '%s' "$QR_JSON" | grep -oE '"setupCode"[[:space:]]*:[[:space:]]*"[^"]+"' | sed 's/.*"setupCode"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
if [ -z "$BOOTSTRAP_B64" ]; then
  echo "[setup-gateway] qr did not produce a setupCode. Output:" >&2
  echo "$QR_JSON" >&2
  exit 1
fi
BOOTSTRAP_DECODED="$(printf '%s' "$BOOTSTRAP_B64" | base64 -d 2>/dev/null)"
BOOTSTRAP_TOKEN="$(printf '%s' "$BOOTSTRAP_DECODED" | grep -oE '"bootstrapToken"[[:space:]]*:[[:space:]]*"[^"]+"' | sed 's/.*"bootstrapToken"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')"
if [ -z "$BOOTSTRAP_TOKEN" ]; then
  echo "[setup-gateway] decoded setupCode missing bootstrapToken. Decoded:" >&2
  echo "$BOOTSTRAP_DECODED" >&2
  exit 1
fi
cat > "$SETUP_CODE_OUT" <<JSON
{
  "gatewayUrl": "ws://localhost:${GATEWAY_PORT}",
  "bootstrapToken": "${BOOTSTRAP_TOKEN}"
}
JSON
echo "[setup-gateway] setup-code written to ${SETUP_CODE_OUT}" >&2

echo "setup-gateway.sh OK"
