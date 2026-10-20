using MediaEngine.AI.Infrastructure;

namespace MediaEngine.AI.Tests;

/// <summary>Hand-written <see cref="IAiRuntimeStatus"/> that answers whether the native runtimes load without touching any files.</summary>
internal sealed class FixedRuntimeStatus(bool textLoads, bool audioLoads = false) : IAiRuntimeStatus
{
    public bool TextRuntimeLoads { get; set; } = textLoads;

    public bool AudioRuntimeLoads { get; set; } = audioLoads;

    public bool RefreshTextRuntime() => TextRuntimeLoads;
}
