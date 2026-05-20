# Fake LLM Server

Minimal OpenAI-compatible HTTP mock used by the gateway-compat tests to drive
the openclaw gateway without burning real provider credit.

## Scope

| Endpoint                         | Purpose                                                       |
|----------------------------------|---------------------------------------------------------------|
| `POST /v1/chat/completions`      | Canned non-streaming completion that echoes the last user msg |
| `GET  /__assert/last-request`    | Returns `{ lastRequest, requestCount }` for test assertions   |
| `POST /__assert/reset`           | Clears recorded state                                         |
| `GET  /`                         | Health probe (`200 ok`)                                       |

Streaming, tool calls, and Anthropic-shape `/v1/messages` are intentionally
out of scope until a scenario needs them (see plan workstream W2).

## Run

```sh
node tools/fake-llm-server/server.mjs
```

Env vars: `FAKE_LLM_PORT` (default `18888`), `FAKE_LLM_BIND` (default
`127.0.0.1`), `FAKE_LLM_MODEL` (default `fake-llm`).

## Configure openclaw to use it

The W0 spike workflow (`.github/workflows/gateway-compat-spike.yml`) is the
authoritative source for the correct provider config shape — it runs
`openclaw config validate` so any shape drift fails loudly. Approximate
shape:

```sh
openclaw config set agents.providers.fake.api openai-completions
openclaw config set agents.providers.fake.baseUrl http://127.0.0.1:18888/v1
openclaw config set agents.providers.fake.apiKey test
openclaw config set agents.providers.fake.models.0.id fake-llm
openclaw config set agents.defaults.model.primary fake/fake-llm
openclaw config validate
```
