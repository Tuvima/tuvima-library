namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>
/// Where the Engine writes its technical log files (<c>tuvima-yyyyMMdd.log</c>, plus <c>_001</c> style names when a
/// day's file passes 50 MB). One place so the Serilog setup, the reader and the retention service agree.
/// </summary>
public sealed record EngineLogLocation(string Directory)
{
    /// <summary>The file name pattern Serilog's rolling file sink produces for the Engine log.</summary>
    public const string FilePattern = "tuvima-*.log";

    /// <summary>The path stem Serilog is given; it inserts the date before the extension.</summary>
    public const string SinkFileName = "tuvima-.log";

    /// <summary><c>TUVIMA_LOG_DIR</c>, or a <c>logs</c> folder beside the Engine when it is not set.</summary>
    public static EngineLogLocation FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("TUVIMA_LOG_DIR") ?? "logs");

    /// <summary>How many log files are on disk and how many bytes they take together.</summary>
    public (int FileCount, long TotalBytes) Measure()
    {
        var path = Path.GetFullPath(Directory);
        if (!System.IO.Directory.Exists(path))
        {
            return (0, 0);
        }

        var files = new DirectoryInfo(path).EnumerateFiles(FilePattern, SearchOption.TopDirectoryOnly).ToList();
        return (files.Count, files.Sum(file => file.Length));
    }
}
