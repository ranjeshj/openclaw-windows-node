using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Structural assertions on installer and build-layout contracts. These pin
/// behavior that cannot be exercised by a small in-process unit test because it
/// depends on installer packaging or the developer build graph.
///
/// Round 2 (Scott #5) — AppMutex coordination prevents the Inno uninstaller
/// from racing the running tray on shared state (settings.json,
/// gateways.json, device-key-ed25519.json, Logs/).  The mutex name must
/// match App.xaml.cs's single-instance mutex.
/// </summary>
public sealed class InstallerIssAssertionTests
{
    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    [Fact]
    public void Installer_HasAppMutexMatchingTraySingleInstance()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));
        Assert.Contains("AppMutex=OpenClawTray", iss);

        // The matching tray-side mutex name must be present in App.xaml.cs.
        var appXamlCs = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("var mutexName = \"OpenClawTray\";", appXamlCs);
    }

    [Fact]
    public void Installer_RecursivelyCopiesPreparedPublishPayload()
    {
        var iss = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "installer.iss"));

        Assert.Contains("Source: \"{#publish}\\*\"; DestDir: \"{app}\"; Flags: ignoreversion recursesubdirs", iss);
    }

    [Fact]
    public void ReleaseWorkflow_PublishesAndSignsSetupEnginePayload()
    {
        var workflow = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("- name: Publish SetupEngine.UI", workflow);
        Assert.Contains("mkdir publish\\SetupEngine", workflow);
        Assert.Contains("copy publish-setup\\* publish\\SetupEngine\\ -Recurse", workflow);

        Assert.Matches(
            new Regex(
                @"- name: Sign Executable[\s\S]*?files-folder: publish[\s\S]*?files-folder-filter: exe[\s\S]*?files-folder-recurse: true",
                RegexOptions.Multiline),
            workflow);

        Assert.Matches(
            new Regex(
                @"- name: Sign ARM64 Executables[\s\S]*?files-folder: artifacts/tray-win-arm64[\s\S]*?files-folder-filter: exe[\s\S]*?files-folder-recurse: true",
                RegexOptions.Multiline),
            workflow);
    }

    [Fact]
    public void CommandPalettePackage_UsesOpenClawBrandingAndTargetedUninstall()
    {
        var repositoryRoot = GetRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(repositoryRoot, "installer.iss"));
        var commandPaletteManifest = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "OpenClaw.CommandPalette", "Package.appxmanifest"));

        Assert.Contains("DisplayName>OpenClaw Command Palette<", commandPaletteManifest);
        Assert.Contains("PublisherDisplayName>Scott Hanselman<", commandPaletteManifest);
        Assert.Contains("Get-AppxPackage -Name 'OpenClaw'", installer);
        Assert.DoesNotContain("Get-AppxPackage -Name '*OpenClaw*'", installer);
        Assert.Contains("RunOnceId: \"remove-command-palette-package\"", installer);
    }

    [Fact]
    public void DevelopmentBuild_StagesSetupEngineFromProjectFiles()
    {
        var repositoryRoot = GetRepositoryRoot();
        var trayProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var setupProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "OpenClaw.SetupEngine.UI", "OpenClaw.SetupEngine.UI.csproj"));

        Assert.Contains("StageSetupEngineIntoTrayLayout", trayProject);
        Assert.Contains("..\\OpenClaw.SetupEngine.UI\\OpenClaw.SetupEngine.UI.csproj", trayProject);
        Assert.Contains("$(TargetDir)SetupEngine", trayProject);
        Assert.DoesNotContain("ProjectReference Include=\"..\\OpenClaw.SetupEngine.UI\\OpenClaw.SetupEngine.UI.csproj\"", trayProject);
        Assert.DoesNotContain("StageSetupEngineIntoTrayLayout", setupProject);
    }

    [Fact]
    public void SetupEnginePublish_CopiesGeneratedWinUiArtifacts()
    {
        var setupProject = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "OpenClaw.SetupEngine.UI.csproj"));

        Assert.Contains("CopyGeneratedWinUiArtifactsToPublish", setupProject);
        Assert.Contains("AfterTargets=\"Publish\"", setupProject);
        Assert.Contains("**\\*.xbf", setupProject);
        Assert.Contains("**\\*.pri", setupProject);
        Assert.Contains("$(PublishDir)", setupProject);
    }

    [Fact]
    public void BuildScript_NoLongerCopiesSetupEngineIntoTrayOutput()
    {
        var buildScript = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "build.ps1"));

        Assert.DoesNotContain("POST-BUILD: Copy SetupEngine.UI into WinUI output so the tray can find it", buildScript);
        Assert.DoesNotContain("Copy-Item \"$setupOutDir\\*\" $destDir -Recurse -Force", buildScript);
    }

}
