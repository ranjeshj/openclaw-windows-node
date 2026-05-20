using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Enforces that the Last-Known-Good gateway version constants in
/// <see cref="GatewayLkg"/> stay in lockstep with <c>gateway-lkg.json</c> at the
/// repository root. The JSON file is the source of truth for tooling (CI auto-bump
/// workflow, dev scripts); the C# constants are the source of truth at compile time
/// for the tray binary. The auto-bump PR must update both together; this test fails
/// loudly if they drift.
/// </summary>
public class GatewayLkgTests
{
    [Fact]
    public void Version_Matches_GatewayLkgJson()
    {
        var json = LoadLkgJson();
        var version = json.RootElement.GetProperty("version").GetString();

        Assert.False(string.IsNullOrWhiteSpace(version),
            "gateway-lkg.json must have a non-empty 'version' string.");
        Assert.Equal(GatewayLkg.Version, version);
    }

    [Fact]
    public void VerifiedAt_Matches_GatewayLkgJson()
    {
        var json = LoadLkgJson();
        var verifiedAt = json.RootElement.GetProperty("verifiedAt").GetString();

        Assert.False(string.IsNullOrWhiteSpace(verifiedAt),
            "gateway-lkg.json must have a non-empty 'verifiedAt' ISO-8601 timestamp.");
        Assert.Equal(GatewayLkg.VerifiedAt, verifiedAt);
    }

    [Fact]
    public void VerifiedTrayRef_Matches_GatewayLkgJson()
    {
        var json = LoadLkgJson();
        var verifiedTrayRef = json.RootElement.GetProperty("verifiedTrayRef").GetString();

        Assert.False(string.IsNullOrWhiteSpace(verifiedTrayRef),
            "gateway-lkg.json must have a non-empty 'verifiedTrayRef' string.");
        Assert.Equal(GatewayLkg.VerifiedTrayRef, verifiedTrayRef);
    }

    [Fact]
    public void Version_ShapeIsRecognizable()
    {
        // The gateway is published as the 'openclaw' npm package using a CalVer-ish
        // YYYY.M.D scheme (e.g. "2026.5.17"). Reject obvious accidents like an empty
        // string, a dist-tag ("latest"), or whitespace before they can reach a release.
        Assert.False(string.IsNullOrWhiteSpace(GatewayLkg.Version));
        Assert.DoesNotContain(' ', GatewayLkg.Version);
        Assert.False(GatewayLkg.Version.Equals("latest", StringComparison.OrdinalIgnoreCase),
            "GatewayLkg.Version must pin a concrete published version, not a dist-tag.");
        Assert.False(GatewayLkg.Version.Equals("beta", StringComparison.OrdinalIgnoreCase),
            "GatewayLkg.Version must pin a concrete published version, not a dist-tag.");
        Assert.Contains('.', GatewayLkg.Version);
    }

    [Fact]
    public void VersionOverrideEnvironmentVariable_IsStable()
    {
        // Docs, CI workflows, and dev scripts hard-code this name. Guard against
        // accidental renames by snapshotting it here.
        Assert.Equal("OPENCLAW_GATEWAY_VERSION", GatewayLkg.VersionOverrideEnvironmentVariable);
    }

    private static JsonDocument LoadLkgJson()
    {
        var path = Path.Combine(GetRepositoryRoot(), "gateway-lkg.json");
        Assert.True(File.Exists(path), $"gateway-lkg.json must exist at the repo root ({path}).");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
        {
            return envRepoRoot;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root from " + AppContext.BaseDirectory +
            ". Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
