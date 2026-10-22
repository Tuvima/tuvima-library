namespace MediaEngine.Domain.Contracts;

/// <summary>The kinds of file fingerprint a stash-box record can carry (Videos spec D37).</summary>
public static class AdultFingerprintAlgorithms
{
    /// <summary>A short hash of the file's first and last blocks; identifies an exact copy.</summary>
    public const string Oshash = "OSHASH";

    /// <summary>A full-file hash; identifies an exact copy.</summary>
    public const string Md5 = "MD5";

    /// <summary>A perceptual hash; recognises the same video after it was re-encoded or resized.</summary>
    public const string Phash = "PHASH";

    public static bool IsValid(string? value) => value is Oshash or Md5 or Phash;
}

/// <summary>One fingerprint of a file, computed on this machine. It leaves the machine only with the fingerprint permission.</summary>
public sealed record AdultFingerprint(string Algorithm, string Hash);

/// <summary>What an adult-only lookup is asked. Title is always allowed; fingerprints are used only with the permission.</summary>
/// <param name="Title">The video's title as the Library has it (file-derived or edited).</param>
/// <param name="Fingerprints">Fingerprints of the file. Ignored, and never sent, unless the administrator turned the permission on.</param>
/// <param name="PreferredServerId">The server the Vault library chose; it is tried first, the others follow in their saved order.</param>
/// <param name="ProductCode">A studio product code read from the file or folder name (for example ABC-123), if any.</param>
/// <param name="DurationSeconds">The file's length in seconds, if known. A code or a title is only a strong match when the length agrees.</param>
/// <param name="Date">A date read from the file name, if any. When both sides have one it must agree.</param>
public sealed record AdultLookupQuery(
    string? Title,
    IReadOnlyList<AdultFingerprint> Fingerprints,
    string? PreferredServerId = null,
    string? ProductCode = null,
    double? DurationSeconds = null,
    DateOnly? Date = null);

/// <summary>How a match was decided (Videos spec D37). Stored with the match by the Vault lookup path.</summary>
public static class AdultMatchSignals
{
    public const string Fingerprint = "fingerprint";
    public const string ProductCode = "product_code";
    public const string Title = "title";

    /// <summary>A person picked the record in Identify (Videos spec D38).</summary>
    public const string Identify = "identify";

    public static bool IsValid(string? value) => value is Fingerprint or ProductCode or Title or Identify;
}

/// <summary>
/// What an adult-only lookup brought back. Every field is optional, and all of it is untrusted text that has been length-limited
/// and stripped of control characters before it gets here.
/// </summary>
public sealed record AdultVideoInfo(
    string ServerId,
    string RecordId,
    string MatchedBy,
    string? Title,
    string? Description,
    DateOnly? Date,
    string? ProductCode,
    string? Studio,
    IReadOnlyList<string> Performers,
    IReadOnlyList<string> Tags,
    byte[]? Cover,
    string? CoverMimeType)
{
    /// <summary>The record's length in seconds, when the server knows it.</summary>
    public int? DurationSeconds { get; init; }
}

/// <summary>
/// A lookup that only ever answers about Vault items (the provider declares <c>adult_only</c>). It returns null when the
/// plugin is off, has no working key, finds nothing, or the server is unavailable: the library then behaves as if the lookup
/// were set to None. Callers must ask <see cref="IProviderAudienceGate"/> first; the lookup does not know which item it is for.
/// </summary>
public interface IAdultVideoLookup
{
    /// <summary>The provider's name in <c>config/providers</c>.</summary>
    string ProviderName { get; }

    Task<AdultVideoInfo?> LookupAsync(AdultLookupQuery query, CancellationToken ct = default);
}

/// <summary>What Identify searches for (Videos spec D38): the text the person typed, a product code, or both.</summary>
public sealed record AdultCandidateQuery(string? Text, string? ProductCode, string? PreferredServerId = null);

/// <summary>
/// The manual side of an adult-only provider: candidates for Identify, and one chosen record read again by its id (so an applied
/// value always comes from the server, never from what a browser sent). Like <see cref="IAdultVideoLookup"/>, callers must ask
/// <see cref="IProviderAudienceGate"/> first.
/// </summary>
public interface IAdultVideoSearch
{
    string ProviderName { get; }

    /// <summary>Records that might be this video. Never downloads artwork. Empty when the plugin is off, has no working key, or finds nothing.</summary>
    Task<IReadOnlyList<AdultVideoInfo>> SearchAsync(AdultCandidateQuery query, CancellationToken ct = default);

    /// <summary>One record by its server and id, with its cover downloaded and checked. Null when it is not found.</summary>
    Task<AdultVideoInfo?> GetAsync(string serverId, string recordId, CancellationToken ct = default);
}

/// <summary>
/// The one place that decides which providers may be asked about which items (Videos spec D21, D26). Vault items are asked about
/// by adult-only providers and by nobody else; every other item is never asked about by an adult-only provider.
/// </summary>
public interface IProviderAudienceGate
{
    /// <summary>May this provider be asked about this media asset or work? An unknown provider is never allowed.</summary>
    Task<bool> MayAskAboutItemAsync(string providerName, Guid assetOrWorkId, CancellationToken ct = default);

    /// <summary>May this provider be asked about files that are about to be added to this library (before an item exists)?</summary>
    Task<bool> MayAskAboutLibraryAsync(string providerName, string? libraryId, CancellationToken ct = default);
}

/// <summary>The plain-words permissions and servers of the Stash-box plugin (administrator only). Keys are never returned.</summary>
public sealed record StashBoxServer(string Id, string Name, string Address, bool HasKey, string Connection);

/// <summary>Connection words shown next to a server. Only "connected" is ever shown for a working key; the key itself never is.</summary>
public static class StashBoxConnectionStates
{
    public const string Connected = "connected";
    public const string NotConnected = "not_connected";
    public const string Unknown = "unknown";
}

/// <summary>What the administrator has set up for the Stash-box plugin. The keys are held in the secrets store and never appear here.</summary>
public sealed record StashBoxSettingsSnapshot(IReadOnlyList<StashBoxServer> Servers, bool FingerprintPermission);

/// <summary>
/// The Stash-box plugin's servers (in the order they are tried), the separate fingerprint permission and each account key.
/// Keys live in the secrets store; they can be saved and replaced but are never read back out to a person.
/// </summary>
public interface IStashBoxSettings
{
    Task<StashBoxSettingsSnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>Adds a server by address. Returns null when the address is not allowed (see <c>StashBoxAddress</c>) or is already added.</summary>
    Task<StashBoxServer?> AddServerAsync(string name, string address, CancellationToken ct = default);

    /// <summary>Removes an added server and its key. The default server cannot be removed.</summary>
    Task<bool> RemoveServerAsync(string serverId, CancellationToken ct = default);

    /// <summary>Sets the order servers are tried in. Ids not listed keep their place after the listed ones.</summary>
    Task SetOrderAsync(IReadOnlyList<string> serverIds, CancellationToken ct = default);

    /// <summary>Turns "sends a fingerprint of your file" on or off. Off until the administrator turns it on.</summary>
    Task SetFingerprintPermissionAsync(bool allowed, CancellationToken ct = default);

    /// <summary>The server's address and account key for a request, or null when the server is unknown or has no key.</summary>
    Task<(Uri Address, string Key)?> GetCredentialAsync(string serverId, CancellationToken ct = default);

    Task SetKeyAsync(string serverId, string key, CancellationToken ct = default);

    Task RemoveKeyAsync(string serverId, CancellationToken ct = default);

    /// <summary>Records whether the server accepted the key the last time it was asked.</summary>
    Task SetConnectionAsync(string serverId, string connection, CancellationToken ct = default);
}

/// <summary>The outcome of "Test connection". The message is plain words and never contains the key.</summary>
public sealed record StashBoxTestResult(bool Connected, string Status, string Message);
