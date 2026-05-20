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

**Schema verified against `openclaw 2026.5.18`** by `.github/workflows/gateway-compat-spike.yml` (run 26138294682). The canonical schema can always be re-printed with `openclaw config schema`.

The cleanest way is `openclaw config patch --file ./fake-provider.patch.json5`
with this JSON5 patch:

```json5
{
  models: {
    providers: {
      fake: {
        api: "openai-completions",
        baseUrl: "http://127.0.0.1:18888/v1",
        apiKey: "test",
        authMode: "api-key",
        models: [
          { name: "fake-llm" }
        ]
      }
    }
  },
  agents: {
    defaults: {
      model: { primary: "fake/fake-llm" }
    }
  }
}
```

…then validate:

```sh
openclaw config patch --file ./fake-provider.patch.json5
openclaw config validate    # must exit 0
```

Equivalent `config set` calls (each path verified accepted by the
2026.5.18 schema; the *previous* `agents.providers.fake.*` path that
older docs used is **rejected** — use `models.providers` instead):

```sh
openclaw config set models.providers.fake.api openai-completions
openclaw config set models.providers.fake.baseUrl http://127.0.0.1:18888/v1
openclaw config set models.providers.fake.apiKey test
openclaw config set models.providers.fake.authMode api-key
openclaw config set models.providers.fake.models '[{"name":"fake-llm"}]' --strict-json
openclaw config set agents.defaults.model.primary fake/fake-llm
openclaw config validate
```

