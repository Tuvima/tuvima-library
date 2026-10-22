namespace MediaEngine.Storage.Backup;

/// <summary>
/// What a data store table holds, as far as a backup is concerned. Every table has exactly one kind
/// (see <see cref="BackupTableClassification"/>). A backup keeps settings and a person's own state; it never
/// keeps the catalogue, which a rescan rebuilds, or files and folders.
/// </summary>
public enum BackupDataKind
{
    /// <summary>
    /// Settings and access, kept as rows: accounts, passkeys, profiles, households, grants, view policies and
    /// preferences, provider settings without secrets, notification settings, document types, fields and rules,
    /// backup settings and the Library's own settings rows.
    /// </summary>
    Configuration,

    /// <summary>
    /// A person's own state tied to a title: bookmarks, highlights, reading statistics, listening history, play
    /// stats, reactions, saved items, taste overrides, personal activity and playback positions. Kept, and tied to
    /// the title by recognition data rather than by catalogue row id.
    /// </summary>
    PersonalState,

    /// <summary>
    /// Anything a rescan rebuilds: works, editions, media assets, canonical values, claims, artwork, fingerprints,
    /// subtitles, people, provider caches, search indexes, the review queue and identity or enrichment jobs.
    /// Never kept.
    /// </summary>
    Catalogue,

    /// <summary>
    /// Not kept and not rebuilt from files: sessions and tokens, one-time codes, run, operation and audit logs,
    /// telemetry, pairing requests, sent notifications, job queues, backup copy records and bookkeeping.
    /// </summary>
    Transient,
}
