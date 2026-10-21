namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>One Vault video, image or tile as the Vault screens draw it. Dashboard-only; it is mapped from whatever the Engine sends.</summary>
public sealed record VaultCatalogueItem(string Id, string Title, string? Subtitle, string? CoverUrl, string Route);

/// <summary>
/// What a Vault list or search answered. <see cref="Supported"/> is false while the Engine has no such route yet, so the screens
/// can say the Vault is empty without pretending a search ran.
/// </summary>
public sealed record VaultCatalogueResult(bool Supported, IReadOnlyList<VaultCatalogueItem> Items)
{
    public static VaultCatalogueResult NotYetAvailable { get; } = new(false, []);
}
