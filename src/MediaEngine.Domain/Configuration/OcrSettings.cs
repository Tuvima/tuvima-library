using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Text recognition (OCR) configuration loaded from <c>config/ocr.json</c>. Scanned PDF pages and
/// photos of paper are read by a Tesseract helper running as a child process on this machine.
/// The path defaults to empty, which triggers auto-detection (tools/tesseract/ then PATH).
/// </summary>
public sealed class OcrSettings
{
    /// <summary>Turns text recognition on or off. On by default; it only runs when the helper is found.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Explicit path to the tesseract executable. Leave empty to auto-detect.
    /// An explicit path that does not exist disables recognition instead of silently falling back.
    /// </summary>
    [JsonPropertyName("tesseract_binary_path")]
    public string TesseractBinaryPath { get; set; } = string.Empty;

    /// <summary>"normal" or "low". Low runs the helper at a lower process priority and pauses between files.</summary>
    [JsonPropertyName("effort")]
    public string Effort { get; set; } = OcrEffort.Normal;

    /// <summary>Language codes to read with (Tesseract codes, for example <c>eng</c>). Only installed ones are used.</summary>
    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = ["eng"];

    /// <summary>Longest one file may take before it is abandoned (partial text is kept and marked).</summary>
    [JsonPropertyName("per_file_timeout_seconds")]
    public int PerFileTimeoutSeconds { get; set; } = 300;

    /// <summary>Most pages read from one PDF. Longer files are marked as partially read.</summary>
    [JsonPropertyName("max_pages_per_file")]
    public int MaxPagesPerFile { get; set; } = 500;

    /// <summary>A PDF page with fewer non-whitespace characters than this in its text layer is read with OCR.</summary>
    [JsonPropertyName("text_layer_min_characters")]
    public int TextLayerMinCharacters { get; set; } = 25;

    /// <summary>Resolution used to draw PDF pages before recognition.</summary>
    [JsonPropertyName("render_dpi")]
    public int RenderDpi { get; set; } = 300;

    /// <summary>The longest side of a drawn page or photo is reduced to this many pixels.</summary>
    [JsonPropertyName("max_render_side_pixels")]
    public int MaxRenderSidePixels { get; set; } = 3500;

    /// <summary>Images with a shorter side than this are too small to hold readable text and are skipped.</summary>
    [JsonPropertyName("min_image_side_pixels")]
    public int MinImageSidePixels { get; set; } = 200;

    /// <summary>
    /// A first pass below this average confidence (0-100) triggers orientation detection and one retry.
    /// </summary>
    [JsonPropertyName("rotation_retry_confidence")]
    public int RotationRetryConfidence { get; set; } = 40;

    /// <summary>Where opt-in language data is downloaded from. {code} is replaced with the language code.</summary>
    [JsonPropertyName("language_download_url")]
    public string LanguageDownloadUrl { get; set; } =
        "https://github.com/tesseract-ocr/tessdata_fast/raw/923915d4ced2a7235221788285785a29c4a42d4a/{code}.traineddata";

    /// <summary>Languages offered for download in Settings (Tesseract code and display name).</summary>
    [JsonPropertyName("available_languages")]
    public List<OcrLanguageOption> AvailableLanguages { get; set; } =
    [
        new() { Code = "eng", Name = "English" },
        new() { Code = "spa", Name = "Spanish" },
        new() { Code = "fra", Name = "French" },
        new() { Code = "deu", Name = "German" },
        new() { Code = "ita", Name = "Italian" },
        new() { Code = "por", Name = "Portuguese" },
        new() { Code = "nld", Name = "Dutch" },
        new() { Code = "pol", Name = "Polish" },
        new() { Code = "swe", Name = "Swedish" },
        new() { Code = "tur", Name = "Turkish" },
        new() { Code = "rus", Name = "Russian" },
        new() { Code = "ara", Name = "Arabic" },
        new() { Code = "hin", Name = "Hindi" },
        new() { Code = "jpn", Name = "Japanese" },
        new() { Code = "kor", Name = "Korean" },
        new() { Code = "chi_sim", Name = "Chinese (Simplified)" },
        new() { Code = "chi_tra", Name = "Chinese (Traditional)" },
    ];
}

/// <summary>A language the person can choose to download for text recognition.</summary>
public sealed class OcrLanguageOption
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Allowed values for <see cref="OcrSettings.Effort"/>.</summary>
public static class OcrEffort
{
    public const string Low = "low";
    public const string Normal = "normal";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Low, StringComparison.OrdinalIgnoreCase) ? Low : Normal;
}
