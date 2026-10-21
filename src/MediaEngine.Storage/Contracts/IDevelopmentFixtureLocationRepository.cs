namespace MediaEngine.Storage.Contracts;

/// <summary>Where a file the Engine first found at <paramref name="DetectedPath"/> is now, and the files it wrote for it.</summary>
/// <param name="DetectedPath">The path the file had when ingestion first found it.</param>
/// <param name="CurrentPath">The path of its media asset now, after any filing.</param>
/// <param name="GeneratedFiles">Subtitle and lyric files the Engine wrote for that asset (never user-owned ones).</param>
public sealed record DevelopmentFixtureLocation(
    string DetectedPath,
    string CurrentPath,
    IReadOnlyList<string> GeneratedFiles);

/// <summary>
/// Follows development fixtures to where ingestion filed them, so a development reset can delete exactly the files
/// it created even after they were moved and renamed.
/// </summary>
public interface IDevelopmentFixtureLocationRepository
{
    /// <summary>
    /// The current location of each of <paramref name="detectedPaths"/> that became a media asset. Paths are compared
    /// as full paths, ignoring case; paths that never became an asset are left out.
    /// </summary>
    IReadOnlyList<DevelopmentFixtureLocation> FindCurrentLocations(IReadOnlyCollection<string> detectedPaths);
}
