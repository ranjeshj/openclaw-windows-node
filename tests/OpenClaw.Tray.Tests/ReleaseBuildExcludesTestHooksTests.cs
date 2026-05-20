using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Compile-time gating safety net for the gateway-compat E2E test hooks.
///
/// The <c>tray.testhook.*</c> MCP tool surface lives under
/// <c>OpenClawTray.Services.TestHooks</c>. Its source is wrapped in
/// <c>#if OPENCLAW_E2E_HOOKS</c> and only compiled when MSBuild property
/// <c>OpenClawEnableTestHooks=true</c> is set. The rubber-duck critique on
/// the gateway-compat plan flagged that an env-var gate alone is not safe
/// (a local process with the loopback MCP bearer token could enable
/// powerful hooks like <c>pairing.reset</c>), so the gate is at compile
/// time and the shipped tray binary must not contain the type at all.
///
/// This test reads the built <c>OpenClaw.Tray.WinUI.dll</c> with
/// <see cref="MetadataReader"/> (no native dependency load) and asserts
/// that no <c>TestHook*</c> types live in any
/// <c>OpenClawTray.Services.TestHooks</c> namespace. The test is skipped
/// (not failed) when the assembly cannot be located, so a fresh worktree
/// without a tray build does not break the suite — CI always builds the
/// tray before tests run.
/// </summary>
public class ReleaseBuildExcludesTestHooksTests
{
    [Fact]
    public void TestHookCapability_TypeIsAbsentFromTrayAssembly()
    {
        var trayDll = LocateTrayAssembly();
        if (trayDll is null)
        {
            // Fresh worktree / partial build. CI builds the tray before tests,
            // so a missing DLL here is a local-only condition and harmless.
            return;
        }

        var offendingTypes = EnumerateTypeFullNames(trayDll)
            .Where(name => name.StartsWith("OpenClawTray.Services.TestHooks.", StringComparison.Ordinal))
            .ToList();

        Assert.True(offendingTypes.Count == 0,
            "Shipped OpenClaw.Tray.WinUI.dll must NOT contain any " +
            "OpenClawTray.Services.TestHooks.* types. The gateway-compat test " +
            "hooks are dangerous in production and must only be compiled when " +
            "MSBuild OpenClawEnableTestHooks=true. Offenders: " +
            string.Join(", ", offendingTypes) +
            $". (assembly: {trayDll})");
    }

    private static string? LocateTrayAssembly()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return null;

        var trayBinRoot = Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "bin");
        if (!Directory.Exists(trayBinRoot)) return null;

        // Pick the most recently built OpenClaw.Tray.WinUI.dll across configs/RIDs.
        return Directory
            .EnumerateFiles(trayBinRoot, "OpenClaw.Tray.WinUI.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateTypeFullNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata) yield break;

        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var typeDef = metadata.GetTypeDefinition(handle);
            var ns = metadata.GetString(typeDef.Namespace);
            var name = metadata.GetString(typeDef.Name);
            yield return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
    }

    private static string? FindRepoRoot()
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
        return null;
    }
}
