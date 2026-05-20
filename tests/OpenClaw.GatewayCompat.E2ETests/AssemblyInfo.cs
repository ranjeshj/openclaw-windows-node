// Plan A scenarios drive the full production local-setup flow inside a
// single OpenClawGateway WSL distro. Running multiple test collections
// in parallel causes them to race on `wsl --install OpenClawGateway`
// (one wins, the other sees a partial registration and bails with
// `wsl_existing_distro_unavailable`). Disable assembly-level parallel
// collection execution so the shared `Gateway` collection and the
// per-class `ReconnectFixture` serialize.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
