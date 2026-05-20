# Releasing OpenClaw Windows Hub

This repo uses **GitVersion + CI** for release versioning.  
The canonical release flow is **tag-driven**, not manual file patching.

## TL;DR

1. Merge approved changes into `master`.
2. Create and push a semantic tag:
   ```powershell
   git checkout master
   git pull --ff-only origin master
   git tag -a vX.Y.Z -m "Release vX.Y.Z"
   git push origin master
   git push origin vX.Y.Z
   ```
3. CI (`.github/workflows/ci.yml`) builds/signs/publishes artifacts and creates the GitHub release from that tag.

## Alpha MSIX releases

Alpha tags use the same signed CI release pipeline, but GitHub marks them as pre-releases and not latest releases so normal updater checks do not offer them to users.

```powershell
git tag -a vX.Y.Z-alpha.1 -m "Alpha vX.Y.Z-alpha.1"
git push origin vX.Y.Z-alpha.1
```

The stable MSIX package identity is `OpenClaw.Companion`. Alpha-tagged MSIX packages are patched during CI to use `OpenClaw.Companion.Alpha`, which lets testers install the signed alpha package without upgrading a stable MSIX install. Command Palette packaging remains separate from the MSIX package.

## Why this is the correct flow

- `GitVersion.yml` is configured for `ContinuousDelivery` with `tag-prefix: 'v'`.
- CI computes version from git history/tags and passes it to builds (`-p:Version=...`).
- CI patches MSIX manifest version during build, so releases are consistent across EXE/MSIX assets.

## Important rules

- **Do not manually bump** version files for routine releases:
  - `src/OpenClaw.Tray/OpenClaw.Tray.csproj`
  - `src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj`
  - `src/OpenClaw.Tray.WinUI/Package.appxmanifest`
- Treat csproj `<Version>` as a **local fallback** for dev builds.
- Release versions should come from the **tag** (`vX.Y.Z`).

## Verify release pipeline

After pushing a tag, confirm in GitHub Actions:
- workflow: **Build and Test**
- trigger ref: `refs/tags/vX.Y.Z`
- jobs complete successfully (build, build-msix, release)
- release assets are attached to the tag release

## If you need to retag

If a tag points to the wrong commit:

```powershell
git tag -d vX.Y.Z
git push origin :refs/tags/vX.Y.Z
git tag -a vX.Y.Z -m "Release vX.Y.Z"
git push origin vX.Y.Z
```

## Gateway version (LKG) pinning

The default `openclaw` gateway version the tray installs during local setup
is pinned at the repo root in [`gateway-lkg.json`](../gateway-lkg.json) and
mirrored as compile-time constants in
[`src/OpenClaw.Shared/GatewayLkg.cs`](../src/OpenClaw.Shared/GatewayLkg.cs).
A unit test (`GatewayLkgTests`) fails the build on drift between the two,
so the pair always ships in lockstep.

- **Bumping the pin** is automated by `.github/workflows/gateway-lkg-bump.yml`
  (scheduled poll of the npm `openclaw` package). The workflow runs the full
  `gateway-compat` suite against the candidate version and, only on green,
  opens a PR updating both files. **PRs are never auto-merged** — a human
  CODEOWNER must review the candidate (changelog link, tarball shasum, and
  npm publish time are included in the PR body).
- **Runtime override** (no rebuild required): set
  `OPENCLAW_GATEWAY_VERSION` (e.g. `latest` or `2026.5.18`) before launching
  the tray. Useful for the CI compat matrix and for hands-on validation; the
  shipped installer always uses the pinned LKG by default.
- **Refusing prereleases**: the auto-bump workflow ignores `-alpha`, `-beta`,
  `-rc` dist-tags. Bump them manually if you want them tested.

See `docs/GATEWAY_COMPAT_TESTING.md` for the surrounding compatibility CI
architecture.


