using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Shared;

/// <summary>One action in an <see cref="AppRowMenu"/>.</summary>
public sealed record AppRowMenuItem
{
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public bool Disabled { get; init; }
    public bool Destructive { get; init; }
    public EventCallback OnClick { get; init; }
}
