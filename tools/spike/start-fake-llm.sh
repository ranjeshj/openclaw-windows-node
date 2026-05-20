#!/bin/bash
# Start the fake-LLM HTTP mock inside WSL and verify the chat endpoint
# returns the expected echo. Expects environment:
#   REPO_WSL_PATH  - /mnt/... path to repo root in WSL
#   FAKE_LLM_PORT  - port to bind on 127.0.0.1
set -euo pipefail
: "${REPO_WSL_PATH:?REPO_WSL_PATH must be set}"
: "${FAKE_LLM_PORT:?FAKE_LLM_PORT must be set}"

mkdir -p /home/openclaw/fake-llm
cp "$REPO_WSL_PATH/tools/fake-llm-server/server.mjs" /home/openclaw/fake-llm/server.mjs

# Node ships in Ubuntu-24.04's universe repo as 'nodejs'; install if missing.
if ! command -v node >/dev/null 2>&1; then
  sudo apt-get update -qq
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nodejs
fi
node --version

nohup env FAKE_LLM_PORT="$FAKE_LLM_PORT" FAKE_LLM_BIND=127.0.0.1 \
  node /home/openclaw/fake-llm/server.mjs \
  > /home/openclaw/fake-llm/server.log 2>&1 &
sleep 2

curl -fsS "http://127.0.0.1:$FAKE_LLM_PORT/" && echo
curl -fsS -X POST -H 'content-type: application/json' \
  -d '{"model":"fake-llm","messages":[{"role":"user","content":"spike ping"}]}' \
  "http://127.0.0.1:$FAKE_LLM_PORT/v1/chat/completions"
echo
