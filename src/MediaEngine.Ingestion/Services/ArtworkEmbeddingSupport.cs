namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Conservative physical-file support matrix. A format is admitted only when
/// its writer and TagLib read-back both support front-cover pictures.
/// </summary>
public static class ArtworkEmbeddingSupport
{
    public const int FormatVersion = 1;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".opus",
    };

    public static bool CanEmbed(string? path) =>
        !string.IsNullOrWhiteSpace(path) && AudioExtensions.Contains(Path.GetExtension(path));

    public static bool VerifyFrontCover(string path, ReadOnlySpan<byte> expected)
    {
        using var file = TagLib.File.Create(path);
        foreach (var picture in file.Tag.Pictures)
        {
            if (picture.Type == TagLib.PictureType.FrontCover
                && picture.Data.Data.AsSpan().SequenceEqual(expected))
                return true;
        }
        return false;
    }
}
