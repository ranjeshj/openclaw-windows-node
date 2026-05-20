namespace OpenClaw.Shared;

/// <summary>
/// Last-Known-Good openclaw gateway npm package version that the tray ships with by default.
/// </summary>
/// <remarks>
/// <para>
/// These constants are the single source of truth at compile time for the gateway version
/// the tray installs during local setup. They are baked into consuming assemblies (because
/// they are <c>const</c>) so a tampered <c>gateway-lkg.json</c> after install cannot
/// silently change behavior.
/// </para>
/// <para>
/// Source of truth for tooling (CI auto-bump workflow, dev scripts) is the
/// <c>gateway-lkg.json</c> file at the repo root. A unit test
/// (<c>GatewayLkgTests</c>) enforces that the JSON file and these constants agree, so
/// drift fails the build.
/// </para>
/// <para>
/// Runtime override: set the <c>OPENCLAW_GATEWAY_VERSION</c> environment variable before
/// launching the tray to install a different gateway version (e.g. <c>"latest"</c> or a
/// specific version like <c>"2026.5.18"</c>). Useful for CI matrix runs and hands-on
/// validation; the LKG version remains the default for unattended user installs.
/// </para>
/// </remarks>
public static class GatewayLkg
{
    /// <summary>npm package version string for the LKG gateway.</summary>
    public const string Version = "2026.5.17";

    /// <summary>ISO-8601 UTC timestamp at which this version was last verified by CI.</summary>
    public const string VerifiedAt = "2026-05-19T00:00:00Z";

    /// <summary>Tray git ref (commit SHA or tag) that verified this version.</summary>
    public const string VerifiedTrayRef = "initial-pin";

    /// <summary>Environment variable name to override the LKG version at runtime.</summary>
    public const string VersionOverrideEnvironmentVariable = "OPENCLAW_GATEWAY_VERSION";
}
