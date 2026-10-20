namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>What the Engine knows about text recognition on this machine.</summary>
public sealed record OcrCapability(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? TessdataDirectory,
    IReadOnlyList<string> InstalledLanguages,
    bool HasOrientationData,
    string? Reason,
    bool ExplicitPathMissing = false)
{
    public static OcrCapability Unavailable(string reason) =>
        new(false, null, null, null, [], false, reason);
}

/// <summary>How one recognition call should run.</summary>
public sealed record OcrRunOptions(IReadOnlyList<string> Languages, bool LowPriority, TimeSpan Timeout);

/// <summary>The text read from one image plus how sure the engine was (0-100), when it reports that.</summary>
public sealed record OcrRecognition(string Text, double? AverageConfidence, int WordCount);

/// <summary>
/// Where the helper may be found. Defaults to the real application and system locations.
/// <paramref name="DataRoot"/> is the Tuvima data folder (its <c>tools/tesseract/</c> is searched first);
/// <paramref name="StandardFolders"/> are the operating system's usual install folders, searched last.
/// <paramref name="SystemLanguageDataFolders"/> are the machine-wide language data folders copied from when preparing
/// language data; null means the real ones (<c>TESSDATA_PREFIX</c> and the usual system folders), so tests can pass an
/// empty list and not depend on what happens to be installed.
/// </summary>
public sealed record OcrEnvironment(
    string BaseDirectory,
    IReadOnlyList<string> PathDirectories,
    string? DataRoot = null,
    IReadOnlyList<string>? StandardFolders = null,
    IReadOnlyList<string>? SystemLanguageDataFolders = null)
{
    public static OcrEnvironment Current(string? dataRoot = null) => new(
        AppContext.BaseDirectory,
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        dataRoot,
        StandardInstallFolders());

    private static IReadOnlyList<string> StandardInstallFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            var folders = new List<string>();
            foreach (var (root, name) in new[]
                     {
                         (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR"),
                         (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR"),
                         (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "Tesseract-OCR"),
                     })
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    folders.Add(Path.Combine(root, name));
                }
            }

            return folders;
        }

        return OperatingSystem.IsMacOS() ? ["/opt/homebrew/bin", "/usr/local/bin"] : [];
    }
}

public interface IOcrService
{
    /// <summary>Finds the helper and its language data. Cached briefly; call <see cref="Invalidate"/> after a change.</summary>
    Task<OcrCapability> GetCapabilityAsync(CancellationToken ct = default);

    /// <summary>Reads one prepared image. Throws <see cref="InvalidOperationException"/> when the helper is unavailable.</summary>
    Task<OcrRecognition> RecognizeAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default);

    /// <summary>Asks the helper how many degrees the page must be turned to read upright. Null when unknown.</summary>
    Task<int?> DetectRotationAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default);

    /// <summary>The folder holding installed language data, inside the Tuvima data location.</summary>
    string LanguageDataDirectory { get; }

    /// <summary>Scratch folder for page images; each file's own folder is deleted when that file is done.</summary>
    string TemporaryRoot { get; }

    void Invalidate();
}
