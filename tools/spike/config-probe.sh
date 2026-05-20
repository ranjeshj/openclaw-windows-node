#!/bin/bash
# Discovery probe: dump the canonical JSON schema so we know exactly which
# property paths the gateway CLI accepts. Saves the schema as a separate
# artifact for easy review.
set +e
: "${FAKE_LLM_PORT:?FAKE_LLM_PORT must be set}"

OC=/opt/openclaw/bin/openclaw

dump() {
  echo
  echo "============================================================"
  echo "$@"
  echo "============================================================"
}

dump "openclaw config schema (full JSON schema for openclaw.json)"
# Save to a side file so the spike artifact can include it standalone.
"$OC" config schema > /tmp/openclaw-schema.json 2>&1
echo "schema size: $(wc -c < /tmp/openclaw-schema.json) bytes"
echo "(saved to /tmp/openclaw-schema.json — uploaded as separate artifact)"
# Copy to workspace so the workflow step can grab it.
cp /tmp/openclaw-schema.json "$GITHUB_WORKSPACE/openclaw-schema.json" 2>/dev/null || true

dump "Hunt for 'provider' / 'baseUrl' / 'apiKey' in schema (case-insensitive, with context)"
# These are the keys we care about for a custom OpenAI-compatible provider.
grep -inE 'provider|baseUrl|apiKey|openai-compat' /tmp/openclaw-schema.json | head -60

dump "Hunt for top-level properties"
# Most JSON schemas have a "properties": { ... } block at root.
grep -nE '^\s{2,4}"[a-zA-Z]+"\s*:' /tmp/openclaw-schema.json | head -40

dump "openclaw config patch --help"
"$OC" config patch --help

dump "Attempt: openclaw configure --section model --help"
"$OC" configure --help

dump "agents.defaults.model.primary (known good)"
"$OC" config set agents.defaults.model.primary fake/fake-llm; echo "exit=$?"

dump "Final: openclaw config validate"
"$OC" config validate; echo "validate_exit=$?"

dump "Final config file contents (sanitized)"
CFG_PATH="$HOME/.openclaw/openclaw.json"
if [ -f "$CFG_PATH" ]; then
  sed 's/\(apiKey[^,]*\):"[^"]*"/\1:"<redacted>"/g; s/\(token[^,]*\):"[^"]*"/\1:"<redacted>"/g' "$CFG_PATH"
else
  echo "(config file not yet created at $CFG_PATH)"
fi

