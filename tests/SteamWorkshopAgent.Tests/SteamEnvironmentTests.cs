using SteamWorkshopAgent;

namespace SteamWorkshopAgent.Tests;

public sealed class SteamEnvironmentTests
{
    [Fact]
    public async Task RequireSteamUserAsync_Uses_Explicit_User()
    {
        var environment = new SteamEnvironment(new ProcessRunner());

        var user = await environment.RequireSteamUserAsync(" pardeike ");

        Assert.Equal("pardeike", user);
    }

    [Fact]
    public async Task RequireSteamUserAsync_Uses_Inherited_Environment()
    {
        using var steamUserScope = new EnvironmentVariableScope("STEAMCMD_USER", " pardeike ");
        var environment = new SteamEnvironment(new ProcessRunner());

        var user = await environment.RequireSteamUserAsync(null);

        Assert.Equal("pardeike", user);
    }

    [UnixFact]
    public async Task RequireSteamUserAsync_Falls_Back_To_Shell_Probe()
    {
        var root = Path.Combine(Path.GetTempPath(), "steam-workshop-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shellPath = Path.Combine(root, "fake-shell");
        File.WriteAllText(
            shellPath,
            $"#!/bin/sh\nprintf 'startup output\\n'\nprintf '\\n{SteamEnvironment.SteamUserProbePrefix}%s{SteamEnvironment.SteamUserProbeSuffix}\\n' pardeike\n");
        var chmod = await new ProcessRunner().RunAsync("chmod", ["+x", shellPath], timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(0, chmod.ExitCode);

        try
        {
            using var steamUserScope = new EnvironmentVariableScope("STEAMCMD_USER", null);
            using var shellScope = new EnvironmentVariableScope("SHELL", shellPath);
            var environment = new SteamEnvironment(new ProcessRunner());

            var user = await environment.RequireSteamUserAsync(null);

            Assert.Equal("pardeike", user);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtractSteamUserFromShellOutput_Uses_Sentinel()
    {
        var output = $"""
startup output
{SteamEnvironment.SteamUserProbePrefix}pardeike{SteamEnvironment.SteamUserProbeSuffix}
trailing output
""";

        var user = SteamEnvironment.ExtractSteamUserFromShellOutput(output);

        Assert.Equal("pardeike", user);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            this.name = name;
            originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, originalValue);
        }
    }
}
