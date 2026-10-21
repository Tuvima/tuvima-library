using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Sandbox;

/// <summary>What a sandboxed (WebAssembly) plugin's manifest describes, already checked.</summary>
/// <param name="ModulePath">The full path of the module file inside the plugin's own folder.</param>
/// <param name="Jobs">The recurring jobs the plugin runs; each calls one function exported by the module.</param>
/// <param name="MaxMemoryMb">The most memory the module may use.</param>
/// <param name="MaxSeconds">The longest one run may take, by the clock.</param>
/// <param name="MaxSteps">The most work one run may do (counted in WebAssembly instructions, roughly).</param>
internal sealed record SandboxDefinition(string ModulePath, IReadOnlyList<SandboxJobDefinition> Jobs, int MaxMemoryMb, int MaxSeconds, long MaxSteps)
{
    public const int DefaultMaxMemoryMb = 64;
    public const int DefaultMaxSeconds = 10;
    public const long DefaultMaxSteps = 1_000_000_000;
}

internal sealed record SandboxJobDefinition(string Key, string Name, string Export, PluginSchedule Schedule);
