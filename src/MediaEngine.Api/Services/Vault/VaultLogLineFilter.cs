using MediaEngine.Domain.Services;
using Serilog.Events;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// The Engine's log writers (console and files) leave out any line that spells the name or place of a file in a Vault library
/// (Videos spec D17, D33). The Engine logs ordinary files by path all through ingestion, and the technical log is open to every
/// administrator, so one rule at the sink covers every present and future log call instead of trusting each one to remember. The
/// line is dropped whole (message and the text of any error that came with it); the quiet Vault activity list is where the
/// Vault's own progress is shown, to an unlocked administrator only.
/// </summary>
public static class VaultLogLineFilter
{
    /// <summary>True for a log event that must not be written because it names a Vault location.</summary>
    public static bool NamesVaultLocation(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (logEvent.Properties.ContainsKey(VaultLocationGuard.LogScopeProperty))
        {
            return true;
        }

        if (!VaultLocationGuard.HasVaultFolders)
        {
            return false;
        }

        return VaultLocationGuard.NamesVaultLocation(logEvent.RenderMessage())
            || (logEvent.Exception is { } error && VaultLocationGuard.NamesVaultLocation(error.ToString()));
    }
}
