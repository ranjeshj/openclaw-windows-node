using System;
using Xunit;
using Xunit.Sdk;

namespace OpenClaw.GatewayCompat.E2ETests;

/// <summary>
/// <see cref="FactAttribute"/> that skips the test when
/// <c>OPENCLAW_RUN_GATEWAY_COMPAT</c> is not <c>"1"</c>. Used for scenarios
/// that need WSL + openclaw + the fake LLM running; those run on the
/// gateway-compat CI lane (Windows + WSL) and are skipped locally on dev
/// machines unless explicitly opted in.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class GatewayCompatFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "OPENCLAW_RUN_GATEWAY_COMPAT";

    public GatewayCompatFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Set {EnvironmentVariable}=1 to run gateway-compat scenarios " +
                   "(requires WSL + Ubuntu-24.04 + openclaw installed and fake LLM " +
                   "reachable inside the distro).";
        }
    }
}
