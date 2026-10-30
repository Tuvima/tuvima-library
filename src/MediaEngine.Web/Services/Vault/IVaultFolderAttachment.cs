using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Vault;

/// <summary>How adding a folder to the Vault library ended.</summary>
public enum VaultFolderAttachmentResult
{
    /// <summary>The folder is part of the Vault library.</summary>
    Attached,

    /// <summary>The PIN was not accepted (wrong, paused or not set). Nothing was added; the person may try again.</summary>
    PinNotAccepted,

    /// <summary>The Engine did not add the folder (not found, not allowed, not available or not reachable); <see cref="VaultFolderAttachmentOutcome.Message"/> says why.</summary>
    NotAdded,
}

/// <summary>The answer to one attempt to add a folder. <see cref="Message"/> is a plain sentence for everything but success and never names the folder.</summary>
public sealed record VaultFolderAttachmentOutcome(VaultFolderAttachmentResult Result, string? Message = null)
{
    public static VaultFolderAttachmentOutcome Attached { get; } = new(VaultFolderAttachmentResult.Attached);
}

/// <summary>
/// Puts a folder into the Vault library. The Engine does the work (it checks the administrator's own profile PIN, checks the
/// folder, adds it to the Vault library and starts scanning it as Vault); the Dashboard only asks.
/// </summary>
public interface IVaultFolderAttachment
{
    Task<VaultFolderAttachmentOutcome> AttachAsync(VaultLibraryDto library, string folderPath, string adminPin, CancellationToken ct = default);
}

/// <summary>Asks the Engine to add the folder. The folder and the PIN travel only in the request body and are never logged.</summary>
public sealed class EngineVaultFolderAttachment(IEngineApiClient api) : IVaultFolderAttachment
{
    public async Task<VaultFolderAttachmentOutcome> AttachAsync(VaultLibraryDto library, string folderPath, string adminPin, CancellationToken ct = default)
    {
        var call = await api.AttachVaultFolderAsync(library.Id, new AttachVaultFolderRequest { FolderPath = folderPath, AdminPin = adminPin }, ct);
        var message = VaultMessages.For(call);
        return call.Outcome switch
        {
            VaultOutcome.Ok => VaultFolderAttachmentOutcome.Attached,
            VaultOutcome.PinRequired or VaultOutcome.TooManyAttempts
                => new VaultFolderAttachmentOutcome(VaultFolderAttachmentResult.PinNotAccepted, message),
            _ => new VaultFolderAttachmentOutcome(VaultFolderAttachmentResult.NotAdded, message),
        };
    }
}
