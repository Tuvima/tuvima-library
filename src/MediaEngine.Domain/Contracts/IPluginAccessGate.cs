using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Contracts;

/// <summary>Why a plugin or source may or may not be used for something (Plugin architecture spec PA2).</summary>
public enum PluginAccessOutcome
{
    Allowed = 0,

    /// <summary>The person asking is outside the item's audience. Shown like a missing page, never as "you are not allowed".</summary>
    NotInAudience,

    /// <summary>The Vault rule keeps the item away from this Vault item.</summary>
    VaultBlocked,

    /// <summary>An administrator limited the item to other libraries.</summary>
    LibraryLimited,

    /// <summary>No plugin or source has this id, so nothing is allowed.</summary>
    Unknown,
}

/// <summary>What is being asked of a plugin or source, and for whom.</summary>
/// <param name="ItemId">The plugin's manifest id, or the source's name in <c>config/providers</c>.</param>
/// <param name="Requester">
/// The widest audience the person asking belongs to (<see cref="PluginAudience.Administrators"/> for an administrator,
/// <see cref="PluginAudience.Adults"/> for an adult profile, <see cref="PluginAudience.Everyone"/> for a child or any other caller),
/// or <c>null</c> when the Library asked itself (a scan, a schedule). Only the audience rule looks at it.
/// </param>
/// <param name="LibraryId">The library of the item the work is about, when known.</param>
/// <param name="EntityId">A media asset or work id, used to find out whether the item is in the Vault when the library is not known.</param>
public sealed record PluginAccessQuery(string ItemId, PluginAudience? Requester, string? LibraryId, Guid? EntityId);

/// <summary>
/// The one check every plugin and source passes before it is used on something (Plugin architecture spec PA2): who is asking (the
/// item's audience, D1), whether the item is in the Vault (D4) and which libraries an administrator limited the item to (D6).
/// The Vault part never trusts what a caller says about an item; Vault is decided from where the file sits.
/// </summary>
public interface IPluginAccessGate
{
    Task<PluginAccessOutcome> CheckAsync(PluginAccessQuery query, CancellationToken ct = default);
}

/// <summary>
/// Whether a Vault section has "Look up details online" switched on (Vault sections spec D34). Plugins and sources that send details out
/// may work on a Vault item only while it is on. The answer is no for any library that is not a Vault section or whose setting is off.
/// </summary>
public interface IVaultSectionOnlineLookup
{
    Task<bool> IsOnAsync(string libraryId, CancellationToken ct = default);
}

/// <summary>
/// The always-off answer, for tests and any host without the per-section setting: nothing that sends details out ever works on a Vault item.
/// The Engine uses <c>VaultSectionOnlineLookup</c>, which reads the section's stored switch.
/// </summary>
public sealed class VaultSectionOnlineLookupOff : IVaultSectionOnlineLookup
{
    public Task<bool> IsOnAsync(string libraryId, CancellationToken ct = default) => Task.FromResult(false);
}
