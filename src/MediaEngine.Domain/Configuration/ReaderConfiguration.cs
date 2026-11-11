using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Limits for the page reader (PDF and comic reader spec D16-D18), loaded from <c>config/reader.json</c>. The values below apply
/// when the file is missing so a fresh install behaves the same.
/// </summary>
public sealed class ReaderConfiguration
{
    [JsonPropertyName("comic")]
    public ComicReaderConfiguration Comic { get; set; } = new();
}

/// <summary>How comic archives are opened and how page pictures are made.</summary>
public sealed class ComicReaderConfiguration
{
    /// <summary>The only widths, in pixels, a page picture may be made at. Zero (the original) is always allowed.</summary>
    [JsonPropertyName("widths")]
    public int[] Widths { get; set; } = [160, 480, 720, 1080, 1440, 2160];

    /// <summary>The fixed small size used by the page slider and the Pages panel.</summary>
    [JsonPropertyName("thumbnail_width")]
    public int ThumbnailWidth { get; set; } = 160;

    /// <summary>An archive with more picture entries than this is refused.</summary>
    [JsonPropertyName("max_entries")]
    public int MaxEntries { get; set; } = 6000;

    /// <summary>A page larger than this many megabytes is refused (and so is the whole comic when it is the cover).</summary>
    [JsonPropertyName("max_page_megabytes")]
    public int MaxPageMegabytes { get; set; } = 60;

    /// <summary>A page with more pixels than this (in millions) is refused.</summary>
    [JsonPropertyName("max_page_megapixels")]
    public int MaxPageMegapixels { get; set; } = 120;

    /// <summary>A CBR or CB7 whose pictures add up to more than this many megabytes is refused instead of unpacked.</summary>
    [JsonPropertyName("max_unpacked_megabytes")]
    public int MaxUnpackedMegabytes { get; set; } = 4096;

    /// <summary>Size of the temporary folder holding unpacked comics. The oldest comic is cleared first.</summary>
    [JsonPropertyName("cache_size_mb")]
    public int CacheSizeMb { get; set; } = 2048;

    [JsonPropertyName("jpeg_quality")]
    public int JpegQuality { get; set; } = 85;
}
