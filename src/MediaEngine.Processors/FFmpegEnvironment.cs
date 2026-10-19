namespace MediaEngine.Processors;

/// <summary>
/// The places the FFmpeg search looks beyond the explicit setting. Split out so tests can point the search at
/// temporary folders instead of the real machine.
/// </summary>
/// <param name="BaseDirectory">The app folder; the search also walks up from it looking for <c>tools/ffmpeg/</c>.</param>
/// <param name="DataRoot">The Tuvima data folder; <c>tools/ffmpeg/</c> inside it is searched first. Null skips it.</param>
/// <param name="PathDirectories">The folders on the system PATH.</param>
/// <param name="StandardFolders">The operating system's usual install folders for FFmpeg.</param>
public sealed record FFmpegEnvironment(
    string BaseDirectory,
    string? DataRoot,
    IReadOnlyList<string> PathDirectories,
    IReadOnlyList<string> StandardFolders)
{
    public static FFmpegEnvironment Current(string? dataRoot) => new(
        AppContext.BaseDirectory,
        dataRoot,
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StandardInstallFolders());

    private static IReadOnlyList<string> StandardInstallFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folders = new List<string>();
            AddIfKnown(folders, programFiles, "ffmpeg", "bin");
            folders.Add(@"C:\ffmpeg\bin");
            AddIfKnown(folders, localAppData, "Microsoft", "WinGet", "Links");
            AddIfKnown(folders, programData, "chocolatey", "bin");
            AddIfKnown(folders, profile, "scoop", "shims");
            return folders;
        }

        return OperatingSystem.IsMacOS() ? ["/opt/homebrew/bin", "/usr/local/bin"] : [];
    }

    private static void AddIfKnown(List<string> folders, string root, params string[] parts)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            folders.Add(Path.Combine([root, .. parts]));
        }
    }
}
