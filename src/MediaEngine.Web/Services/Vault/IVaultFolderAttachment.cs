using MediaEngine.Contracts.Vault;

namespace MediaEngine.Web.Services.Vault;

/// <summary>How adding a folder to the Vault library ended.</summary>
public enum VaultFolderAttachmentResult
{
    /// <summary>The folder is part of the Vault library.</summary>
    Attached,

    /// <summary>The Engine cannot attach a folder to a Vault library yet, so nothing was added.</summary>
    NotYetSupported,
}

/// <summary>
/// Puts a folder into the Vault library. SEAM (Videos spec WP11/WP12): today the Engine only records that a Vault library exists
/// (<c>POST /api/v1/vault/libraries</c> writes the library row and nothing else); the folder, the library name, its
/// <c>libraries.json</c> entry and the scan belong to the Vault ingestion work. <see cref="PendingVaultFolderAttachment"/> therefore
/// reports <see cref="VaultFolderAttachmentResult.NotYetSupported"/> and sends nothing. When the Engine gains that route, only the
/// implementation changes.
/// </summary>
public interface IVaultFolderAttachment
{
    Task<VaultFolderAttachmentResult> AttachAsync(VaultLibraryDto library, string folderPath, CancellationToken ct = default);
}

/// <summary>The stand-in until the Engine can attach a folder to a Vault library. Makes no request.</summary>
public sealed class PendingVaultFolderAttachment : IVaultFolderAttachment
{
    public Task<VaultFolderAttachmentResult> AttachAsync(VaultLibraryDto library, string folderPath, CancellationToken ct = default) =>
        Task.FromResult(VaultFolderAttachmentResult.NotYetSupported);
}
