namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>What the Engine knows about text recognition on this machine.</summary>
public sealed record OcrCapability(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? TessdataDirectory,
    IReadOnlyList<string> InstalledLanguages,
    bool HasOrientationData,
    string? Reason)
{
    public static OcrCapability Unavailable(string reason) =>
        new(false, null, null, null, [], false, reason);
}

/// <summary>How one recognition call should run.</summary>
public sealed record OcrRunOptions(IReadOnlyList<string> Languages, bool LowPriority, TimeSpan Timeout);

/// <summary>The text read from one image plus how sure the engine was (0-100), when it reports that.</summary>
public sealed record OcrRecognition(string Text, double? AverageConfidence, int WordCount);

/// <summary>Where the helper may be found. Defaults to the real application and system locations.</summary>
public sealed record OcrEnvironment(string BaseDirectory, IReadOnlyList<string> PathDirectories)
{
    public static OcrEnvironment Current() => new(
        AppContext.BaseDirectory,
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
