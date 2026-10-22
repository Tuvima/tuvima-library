namespace MediaEngine.Domain.Configuration;

/// <summary>How a library's kind decides what it may look up outside the machine.</summary>
public enum LibraryLookupMode
{
    /// <summary>No kind decides (the Vault's own entries, a kind this list does not know, or a kind that looks details up): the library's own fields apply, as they always have.</summary>
    FromFields,

    /// <summary>A plain kind (lookup "none": Other videos, Other audio, Other reading): local identity only, never any outside contact.</summary>
    Plain,

    /// <summary>The YouTube kind: local identity, plus a YouTube lookup by video ID.</summary>
    YouTube,
}

/// <summary>What one library may look up, once its kind has had its say.</summary>
public sealed record EffectiveLibraryLookup(LibraryLookupMode Mode, string MetadataPolicy, bool YoutubeLookup)
{
    /// <summary>True when no identity job, retail search or Wikidata bridge may ever run for the library's files.</summary>
    public bool BypassesExternalIdentity =>
        Mode != LibraryLookupMode.FromFields || LibraryMetadataPolicies.BypassesExternalIdentity(MetadataPolicy);
}

/// <summary>
/// The one place that turns a library's kind into its lookup behaviour (Library setup spec D17, D18, D38). A plain kind never
/// contacts anything and has the YouTube lookup off whatever its <c>youtube_lookup</c> and <c>metadata_policy</c> fields say; the
/// YouTube kind looks up on YouTube only; every other kind, and a library with no kind, behaves from its own fields.
/// </summary>
public static class LibraryKindLookupPolicy
{
    /// <summary>The key of the YouTube kind.</summary>
    public const string YouTubeKindKey = "youtube";

    /// <summary>How the kind named by <paramref name="kindKey"/> decides lookup. A missing list falls back to the kinds that ship, so a plain kind is never read as a lookup kind by accident.</summary>
    public static LibraryLookupMode ModeOf(string? kindKey, IReadOnlyList<LibraryKindDefinition>? kinds)
    {
        if (string.IsNullOrWhiteSpace(kindKey))
        {
            return LibraryLookupMode.FromFields;
        }

        var kind = (kinds ?? LibraryKindsConfiguration.BuiltIn())
            .FirstOrDefault(candidate => string.Equals(candidate.Key, kindKey, StringComparison.Ordinal));
        if (kind is null)
        {
            return LibraryLookupMode.FromFields;
        }

        if (string.Equals(kind.Key, YouTubeKindKey, StringComparison.Ordinal))
        {
            return LibraryLookupMode.YouTube;
        }

        return kind.Lookup.IsNone ? LibraryLookupMode.Plain : LibraryLookupMode.FromFields;
    }

    /// <summary>The library's lookup behaviour: the kind's, where the kind decides, otherwise the library's own fields.</summary>
    public static EffectiveLibraryLookup For(LibraryFolderConfig library, IReadOnlyList<LibraryKindDefinition>? kinds)
    {
        ArgumentNullException.ThrowIfNull(library);
        var mode = ModeOf(library.KindKey, kinds);
        return mode switch
        {
            LibraryLookupMode.Plain => new EffectiveLibraryLookup(mode, LocalPolicy(library.MetadataPolicy), YoutubeLookup: false),
            LibraryLookupMode.YouTube => new EffectiveLibraryLookup(mode, LocalPolicy(library.MetadataPolicy), YoutubeLookup: true),
            _ => new EffectiveLibraryLookup(mode, library.MetadataPolicy, library.YoutubeLookup),
        };
    }

    /// <summary>True when nothing may be asked of any outside provider for this library's files.</summary>
    public static bool BypassesExternalIdentity(LibraryFolderConfig library, IReadOnlyList<LibraryKindDefinition>? kinds) =>
        For(library, kinds).BypassesExternalIdentity;

    // A policy that already keeps identity local (local_only, manual) stays as it is; anything else becomes local_only.
    private static string LocalPolicy(string? policy) =>
        LibraryMetadataPolicies.BypassesExternalIdentity(policy) ? policy! : LibraryMetadataPolicies.LocalOnly;
}
