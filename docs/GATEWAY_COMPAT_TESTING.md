# Gateway Compatibility Testing

> Goal: catch openclaw gateway regressions before they hit Windows tray
> users, and pin the tray installer to a Last-Known-Good (LKG) gateway
> version that we have verified end-to-end.

This document is the operator-facing companion to the implementation plan
in `C:\repos\copilotdocs\gateway-compat-ci-plan.md`. The plan covers
**why**; this document covers **how to use** the resulting CI.

## Pieces

| File / surface                                          | Purpose |
|---------------------------------------------------------|---------|
| `gateway-lkg.json`                                      | Source of truth for the pinned gateway version (tooling) |
| `src/OpenClaw.Shared/GatewayLkg.cs`                     | Compile-time constants mirrored from the JSON (binary) |
| `OpenClaw.Shared.Tests.GatewayLkgTests`                 | Build-time enforcement that JSON and constants agree |
| `tools/fake-llm-server/`                                | Minimal OpenAI-compatible HTTP mock used by compat tests |
| `.github/workflows/gateway-compat-spike.yml`            | One-shot diagnostic for WSL/openclaw/provider-config shape on real CI (W0) |
| `.github/workflows/gateway-compat.yml` *(planned, W5)*  | Full compat suite (PR: subset vs LKG; nightly: LKG + latest) |
| `.github/workflows/gateway-lkg-bump.yml` *(planned, W5)*| Scheduled poll of `openclaw` npm; opens auto-PR bumping the LKG |
| `src/OpenClaw.Tray.WinUI/Services/TestHooks/` (`#if OPENCLAW_E2E_HOOKS`) | `tray.testhook.*` MCP tools that drive setup/pairing/chat without UI |
| `OpenClaw.Tray.Tests.ReleaseBuildExcludesTestHooksTests`| Asserts shipped tray binary contains no test-hook types |

## Bumping the LKG

Normal flow is fully automated; humans only review the PR.

1. `.github/workflows/gateway-lkg-bump.yml` polls
   `https://registry.npmjs.org/openclaw` every 6 hours.
2. If `dist-tags.latest` differs from `gateway-lkg.json` `version`,
   the workflow runs the full `gateway-compat` suite against the
   candidate version.
3. On green, it opens a PR that updates `gateway-lkg.json` **and**
   `src/OpenClaw.Shared/GatewayLkg.cs` together (the unit test
   enforces drift = build failure).
4. PR body includes:
   - candidate version + tarball shasum + npm publish time
   - changelog link
   - link to the green compat run
5. **PRs are never auto-merged.** A CODEOWNER reviews the candidate
   changelog for behavior changes, then merges.

### Bumping manually

```powershell
$env:OPENCLAW_GATEWAY_VERSION = "2026.6.0"   # candidate
gh workflow run gateway-compat.yml -F gateway_version=$env:OPENCLAW_GATEWAY_VERSION
# Wait for green, then update gateway-lkg.json + GatewayLkg.cs and PR.
```

### Failure triage

When `gateway-lkg-bump.yml` fails:
- The PR is **not** opened; the old LKG stays pinned (tray users are
  unaffected).
- A tracking issue is opened (or updated) describing which scenario
  regressed. File a downstream issue against the gateway repo if
  upstream broke us; otherwise patch the tray.

## Running locally

### Quick sanity

```powershell
./build.ps1
dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore
dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore
```

### Drive a different gateway version

```powershell
$env:OPENCLAW_GATEWAY_VERSION = "latest"  # or e.g. "2026.5.18"
./src/OpenClaw.Tray.WinUI/bin/Debug/net10.0-windows10.0.22621.0/win-arm64/OpenClaw.Tray.WinUI.exe
```

The env var is honored in any build (Debug or Release) to support CI
matrices and hands-on validation; the shipped default is the LKG.

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
