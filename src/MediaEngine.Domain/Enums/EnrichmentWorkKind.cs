namespace MediaEngine.Domain.Enums;

/// <summary>
/// External work classes that need app-level throttling across all library roots.
/// </summary>
public enum EnrichmentWorkKind
{
    RetailProvider,

    /// <summary>Retail lookups for media types whose provider hands out turns slowly (comics): their own slots, so they never queue behind or hold up the others.</summary>
    SlowRetailProvider,
    Wikidata,
    TmdbArtwork,
    WriteBack,
}
