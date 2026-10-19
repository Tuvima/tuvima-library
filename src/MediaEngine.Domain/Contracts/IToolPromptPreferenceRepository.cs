namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Remembers, for the whole server, which missing tools an administrator chose not to be asked about again
/// ("Not now" on the tools banner). The Tools page still offers the install button for them.
/// </summary>
public interface IToolPromptPreferenceRepository
{
    /// <summary>The ids of the tools the banner must not ask about.</summary>
    Task<IReadOnlySet<string>> GetDeclinedAsync(CancellationToken ct = default);

    /// <summary>Records that the banner must not ask about this tool again.</summary>
    Task DeclineAsync(string toolId, CancellationToken ct = default);

    /// <summary>Forgets a decline, so a later problem with the tool is asked about again.</summary>
    Task ClearDeclinedAsync(string toolId, CancellationToken ct = default);
}
