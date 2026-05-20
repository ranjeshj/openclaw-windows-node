using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using OpenClaw.Tray.IntegrationTests; // linked McpClient

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// Per-test-class xUnit fixture that:
/// <list type="number">
///   <item>Provisions isolated %APPDATA%/%LOCALAPPDATA% directories so the
///         tray never reads the developer's real OpenClaw state.</item>
///   <item>Picks a free localhost port for the MCP HTTP server.</item>
///   <item>Spawns the E2E-built tray exe with
///         <c>OPENCLAW_TRAY_E2E=1</c> + <c>OPENCLAW_TRAY_DATA_DIR</c> + the
///         MCP port + (optionally) <c>OPENCLAW_GATEWAY_VERSION</c>.</item>
///   <item>Waits for the tray's <c>mcp-token.txt</c> + HTTP listener.</item>
///   <item>Hands the test class a ready <see cref="McpClient"/> bound to the
///         loopback endpoint with the bearer token configured.</item>
/// </list>
///
/// <para>
/// The fixture deliberately does NOT install WSL or the openclaw gateway —
/// scenarios that need a gateway opt into that via
/// <see cref="GatewayCompatFactAttribute"/>, which skips when
/// <c>OPENCLAW_RUN_GATEWAY_COMPAT</c> is not <c>"1"</c>.
/// </para>
/// </summary>
public sealed class GatewayCompatFixture : IAsyncLifetime
{
    public string DataDir { get; }
    public int McpPort { get; }
    public string McpEndpoint => $"http://127.0.0.1:{McpPort}/mcp";
    public McpClient Client { get; private set; }

    private readonly string _exePath;
    private readonly Process _process;
    private bool _disposed;

    public GatewayCompatFixture()
    {
        DataDir = Path.Combine(Path.GetTempPath(),
            "openclaw-gateway-compat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DataDir);

        McpPort = FindFreePort();
        WriteIsolatedSettings();

        _exePath = LocateE2eTrayExe();
        _process = SpawnTray();

        // Token isn't on disk yet; InitializeAsync replaces this with a
        // token-bearing client once mcp-token.txt appears.
        Client = new McpClient(McpEndpoint);
    }

    public async Task InitializeAsync()
    {
        // Two preconditions before any test can call the MCP endpoint:
        //   1. mcp-token.txt must exist (otherwise our POSTs will 401).
        //   2. GET / returns 200 with that bearer token (tray HTTP listener
        //      is up AND the token matches the on-disk one).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var tokenPath = Path.Combine(DataDir, "mcp-token.txt");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        string? token = null;
        Exception? lastEx = null;

        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Tray process exited before MCP server became ready (exit code {_process.ExitCode}). " +
                    $"Logs: {Path.Combine(DataDir, "openclaw-tray.log")}");
            }

            try
            {
                if (token is null)
                {
                    if (!File.Exists(tokenPath))
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        continue;
                    }
                    token = (await File.ReadAllTextAsync(tokenPath).ConfigureAwait(false)).Trim();
                    if (string.IsNullOrEmpty(token))
                    {
                        token = null;
                        await Task.Delay(500).ConfigureAwait(false);
                        continue;
                    }
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                var resp = await http.GetAsync($"http://127.0.0.1:{McpPort}/").ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    Client.Dispose();
                    Client = new McpClient(McpEndpoint, token);
                    return;
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"E2E tray's MCP server never came up on {McpEndpoint}. Last error: {lastEx?.Message}. " +
            $"Logs: {Path.Combine(DataDir, "openclaw-tray.log")}");
    }

    public Task DisposeAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _disposed = true;

        Client.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch { /* best effort */ }
        finally
        {
            _process.Dispose();
        }
        try { Directory.Delete(DataDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private void WriteIsolatedSettings()
    {
        // Minimal settings.json: MCP-only mode (no gateway WebSocket), no
        // popups, no autostart. Gateway scenarios layer on more via the
        // tray.testhook.* tools rather than baking it here.
        var json = """
        {
          "EnableMcpServer": true,
          "EnableNodeMode": false,
          "SystemRunSandboxEnabled": false,
          "AutoStart": false,
          "GlobalHotkeyEnabled": false,
          "ShowNotifications": false,
          "HasSeenActivityStreamTip": true
        }
        """;
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), json);
    }

    private static string LocateE2eTrayExe()
    {
        // The E2E lane builds the tray with -p:OpenClawEnableTestHooks=true
        // and -p:Configuration=E2E so the artifact lives in its own bin
        // subtree. Locally, devs can also point us at any tray exe via
        // OPENCLAW_E2E_TRAY_EXE.
        var explicitPath = Environment.GetEnvironmentVariable("OPENCLAW_E2E_TRAY_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            var other => throw new PlatformNotSupportedException($"Unsupported process architecture: {other}"),
        };

        var repoRoot = FindRepoRoot();
        var trayProj = Path.Combine(repoRoot, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj");
        var tfm = XDocument.Load(trayProj)
            .Descendants("TargetFramework")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?? throw new InvalidDataException("Could not read TargetFramework from " + trayProj);

        // Prefer an E2E-config build; fall back to Debug if that's the only
        // thing the dev built (the smoke test still works; gateway lane
        // requires an E2E build, but those run on CI).
        foreach (var config in new[] { "E2E", "Debug" })
        {
            var candidate = Path.Combine(
                repoRoot,
                "src", "OpenClaw.Tray.WinUI", "bin", config,
                tfm, rid, "OpenClaw.Tray.WinUI.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not locate an OpenClaw.Tray.WinUI.exe for the gateway-compat harness. " +
            "Build one with: dotnet build src/OpenClaw.Tray.WinUI -c E2E -r " + rid +
            " -p:OpenClawEnableTestHooks=true, " +
            "or set OPENCLAW_E2E_TRAY_EXE to point at an existing exe.");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "openclaw-windows-node.slnx")))
                return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == dir || parent is null) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repo root (openclaw-windows-node.slnx) from " + AppContext.BaseDirectory);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private Process SpawnTray()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = Path.GetDirectoryName(_exePath)!,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.Environment["OPENCLAW_TRAY_DATA_DIR"] = DataDir;
        psi.Environment["OPENCLAW_TRAY_LOCALAPPDATA_DIR"] = DataDir;
        psi.Environment["OPENCLAW_MCP_PORT"] = McpPort.ToString();
        psi.Environment["OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"] = "1";
        // The point of this harness:
        psi.Environment["OPENCLAW_TRAY_E2E"] = "1";
        // Pin the gateway version the tray will install during local-setup.
        // Honored only by Gateway-tier scenarios; smoke tests don't care.
        var gatewayVersion = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_VERSION");
        if (!string.IsNullOrWhiteSpace(gatewayVersion))
        {
            psi.Environment["OPENCLAW_GATEWAY_VERSION"] = gatewayVersion;
        }

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start E2E tray app");
    }
}
