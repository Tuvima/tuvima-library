namespace MediaEngine.Ingestion.Tests.Helpers;

/// <summary>
/// A test that needs the operating system to allow creating a symbolic link. Where it does not (Windows without the privilege), the
/// test is reported as skipped with the reason, never passed and never failed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> WhyNot = new(Probe);

    public SymbolicLinkFactAttribute()
    {
        Skip = WhyNot.Value;
    }

    private static string? Probe()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tuvima_symlink_probe_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "target");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(Path.Combine(folder, "link"), target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return $"This system does not allow creating symbolic links here ({ex.GetType().Name}: {ex.Message}).";
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A probe folder left in the temp folder is harmless; the test result does not depend on it.
                Console.Error.WriteLine($"Could not remove the symbolic-link probe folder {folder}: {ex.Message}");
            }
        }
    }
}
