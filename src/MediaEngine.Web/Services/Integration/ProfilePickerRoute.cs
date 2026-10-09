namespace MediaEngine.Web.Services.Integration;

/// <summary>Where the "Who's using Tuvima?" screen lives and how a sign-in hands over to it.</summary>
public static class ProfilePickerRoute
{
    public const string Path = "/who";

    /// <summary>The picker address that returns to <paramref name="returnUrl"/> once someone is chosen.</summary>
    public static string For(string? returnUrl) =>
        SafeReturnUrl(returnUrl) is { } safe && safe != "/"
            ? $"{Path}?returnUrl={Uri.EscapeDataString(safe)}"
            : Path;

    /// <summary>Only same-site paths are followed after choosing a profile, never another site.</summary>
    public static string? SafeReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal)
        && !value.StartsWith("/\\", StringComparison.Ordinal)
            ? value
            : null;
}
