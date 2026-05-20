# Gateway Compatibility Testing

> Goal: catch openclaw gateway regressions before they hit Windows tray
> users, verified end-to-end via a CI-spawned WSL distro.

This document is the operator-facing companion to the implementation plan
in `C:\repos\copilotdocs\gateway-compat-ci-plan.md`. The plan covers
**why**; this document covers **how to use** the resulting CI.

## Pieces

| File / surface                                          | Purpose |
|---------------------------------------------------------|---------|
| `tools/fake-llm-server/`                                | Minimal OpenAI-compatible HTTP mock used by compat tests |
| `.github/workflows/gateway-compat.yml` *(planned, W5)*  | Full compat suite (PR + nightly) |
| `src/OpenClaw.Tray.WinUI/Services/TestHooks/` (`#if OPENCLAW_E2E_HOOKS`) | `tray.testhook.*` MCP tools that drive setup/pairing/chat without UI |
| `OpenClaw.Tray.Tests.ReleaseBuildExcludesTestHooksTests`| Asserts shipped tray binary contains no test-hook types |

## Running locally

### Quick sanity

```powershell
./build.ps1
dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore
dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore
```

### Drive a specific gateway version

```powershell
$env:OPENCLAW_GATEWAY_VERSION = "latest"  # or e.g. "2026.5.18"
./src/OpenClaw.Tray.WinUI/bin/Debug/net10.0-windows10.0.22621.0/win-arm64/OpenClaw.Tray.WinUI.exe
```

The env var is honored in any build (Debug or Release) to support CI
matrices and hands-on validation; the unset default is `"latest"`.

### Build the tray with test hooks enabled

> ⚠ **For local development only. Never ship this binary.**

```powershell
dotnet build src/OpenClaw.Tray.WinUI -c Debug -r win-arm64 -p:OpenClawEnableTestHooks=true
```

A Release-build smoke test
(`OpenClaw.Tray.Tests.ReleaseBuildExcludesTestHooksTests`) fails loudly
if a build with `-p:OpenClawEnableTestHooks=true` is run against the
production-shape verification.

### Run the fake LLM standalone

```powershell
node tools/fake-llm-server/server.mjs   # listens on 127.0.0.1:18888
```

Point any openai-compatible provider at `http://127.0.0.1:18888/v1`
(any API key works).

## Adding a new compat scenario

1. Decide which `tray.testhook.*` tool you need. If it doesn't exist,
   add it under `src/OpenClaw.Tray.WinUI/Services/TestHooks/` inside the
   `#if OPENCLAW_E2E_HOOKS` block. Add a unit test for the tool itself.
2. Add the new test under `tests/OpenClaw.GatewayCompat.E2ETests/` (one
   file per scenario). The fixture handles tray spawn + isolated AppData
   + fake-LLM lifecycle.
3. Update `.github/workflows/gateway-compat.yml`:
   - if the scenario is fast and stable, add it to the PR subset;
   - otherwise keep it in the nightly-only lane.
4. Document the scenario here.

### Same-path-as-user rule (mandatory)

When you add or modify a `tray.testhook.*` tool, the tool MUST invoke
the same method the matching UI click handler invokes. If the UI handler
does the work inline, extract it into a shared service method first and
have BOTH the handler and the test hook call that method.

This rule exists because gateway-compat's whole purpose is catching
regressions in the production code path a real user hits. A test that
passes against a parallel implementation tells us nothing.

Concrete examples:

| Test hook | Shared method (called by both UI and hook) | UI caller |
|---|---|---|
| `tray.testhook.localSetup.start` | `App.CreateLocalGatewaySetupEngine(...).RunLocalOnlyAsync(...)` | `LocalSetupProgressPage` "Set up locally" handler |
| `tray.testhook.chat.send` | `OpenClawChatDataProvider.SendMessageAsync(...)` | `ChatWindow.OnSendClicked` |
| `tray.testhook.pairing.reset` | `GatewayRegistry.Reset(...)` + per-gateway key wipe (one helper) | Settings page "Reset pairing" button |
| `tray.testhook.connection.waitFor` | `GatewayConnectionManager` state observer (read, no write) | observed by every UI surface that shows connection state |

When you write a new hook, **include a code comment naming the UI
caller and the shared method**. The matching unit test should assert
behavior, not implementation, so a future refactor that consolidates
two handlers into one still passes.

## Extending the fake LLM

The mock at `tools/fake-llm-server/server.mjs` starts intentionally tiny
(one OpenAI-completions endpoint, non-streaming, echoes the user
message). Extend only when a scenario demands it:

- **Streaming**: add `text/event-stream` handling on the same endpoint
  when a scenario asserts streaming UX.
- **Tool calls**: when a `node.invoke` scenario needs the LLM to emit
  tool calls, gate the synthetic call on a sentinel prompt
  (`"call <tool>"`) so existing scenarios stay deterministic.
- **Anthropic `/v1/messages`**: only add if a routing test specifically
  requires it. Most scenarios are served by the openai-compatible path.

Every extension should preserve `/__assert/last-request` so the
harness can keep asserting on what the gateway sent.
