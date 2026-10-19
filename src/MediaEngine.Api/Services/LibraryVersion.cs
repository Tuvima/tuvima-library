using System.Reflection;

namespace MediaEngine.Api.Services;

/// <summary>
/// The one place the Library reads its own version. It comes from the Engine assembly at startup, so there is
/// no hard-coded string to forget to bump.
/// </summary>
public static class LibraryVersion
{
    /// <summary>The Library version without any build suffix (for example <c>1.0.0</c>), or <c>1.0.0</c> when the assembly carries none.</summary>
    public static string Current { get; } = Read(typeof(LibraryVersion).Assembly);

    internal static string Read(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0]           // strip build metadata (e.g. git hash)
        ?? "1.0.0";
}
