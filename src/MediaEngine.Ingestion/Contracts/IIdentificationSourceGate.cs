namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Holds back the files of a library kind that can only be identified by an outside source while no such source works
/// (Plugins and Integrations screens v4 D38): Movies and TV Shows need TMDB or TheTVDB, Comics need Comic Vine. A held file is left
/// where it is, not added as an unidentified item, and is picked up by a later scan once a source works.
/// </summary>
public interface IIdentificationSourceGate
{
    /// <summary>True when <paramref name="fullPath"/> lives in a library whose kind needs an identification source and none works yet.</summary>
    bool IsHeld(string fullPath);
}
