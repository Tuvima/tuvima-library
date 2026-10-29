using MediaEngine.Api.Services.Display;
using MediaEngine.Application.Services;

namespace MediaEngine.Api.Security;

/// <summary>
/// The rules that decide what one viewer may see, in the order the access service applies them (Vault sections spec D32). A later rule
/// can only narrow what an earlier one let through; none can widen it. In particular the Vault comes first: a Vault item is never
/// let back in by the Shared Library, the content limit or anything after them.
/// </summary>
public enum ViewerAccessStage
{
    /// <summary>The Vault and, once sections exist, which Vault sections this session may open.</summary>
    VaultAndSections = 1,

    /// <summary>The Shared Library: the profile's areas, libraries hidden for its household, and libraries hidden from it.</summary>
    SharedLibrary = 2,

    /// <summary>The profile's content limit, with the "Suitable for limited profiles" folder rule for unrated items.</summary>
    ContentLimit = 3,

    /// <summary>The profile's View documents switch: documents stay on the Documents page and out of every shared list.</summary>
    DocumentsOff = 4,

    /// <summary>Personal spaces: a profile's own private View spaces and its own activity.</summary>
    PersonalSpaces = 5,
}

/// <summary>One stage of the access service: what it decides and the type that enforces it today.</summary>
public sealed record ViewerAccessStageInfo(ViewerAccessStage Stage, string Decides, string EnforcedBy);

/// <summary>The stages, in the order they apply. The order is part of the rule and a test pins it.</summary>
public static class ViewerAccessStages
{
    public static IReadOnlyList<ViewerAccessStageInfo> InOrder { get; } =
    [
        new(ViewerAccessStage.VaultAndSections, "A Vault item is absent unless this session unlocked Vault mode; never part of a shared list.",
            $"{nameof(VaultVisibility)} / {nameof(VaultAccessResolver)}"),
        new(ViewerAccessStage.SharedLibrary, "Areas, libraries hidden for the household and libraries hidden for the profile decide which libraries the profile sees.",
            nameof(LibraryVisibilityRule)),
        new(ViewerAccessStage.ContentLimit, "A rated item follows the limit; an unrated one follows allow-unrated or a marked folder.",
            nameof(ContentLimitFilter)),
        new(ViewerAccessStage.DocumentsOff, "Documents are private to the Documents page and need the profile's Documents switch.",
            "View routes (profile documents_enabled); documents never enter the shared catalogue"),
        new(ViewerAccessStage.PersonalSpaces, "A personal space and a profile's own activity are visible to that profile only.",
            "View scopes (ViewProfileAssertion) and PersonalScopeAuthorizer"),
    ];
}

/// <summary>
/// The one service every read for a viewer goes through (Vault sections spec D32, WP0). It extends the existing read path
/// (<see cref="ContentLimitFilter"/>, <see cref="LibraryVisibilityRule"/> and <see cref="AuthorizedDisplayProjectionReadService"/>)
/// instead of replacing it: the authorized display projection already applies the Vault, the Shared Library and the content limit to
/// every list of works, and this service hands the result to the reads that are not lists of works (people, counts) so they start from
/// the same answer. A read that returns people, counts or anything derived from the catalogue asks for the viewer's
/// <see cref="ViewerCatalogue"/> here and takes it as a parameter; it never builds one, and never asks the data store directly what a
/// viewer may see. Reads with no viewer (the outbox, background jobs) cannot ask this service and keep the Vault out with the
/// <c>VaultSql</c> predicates instead. <c>ViewerAccessGuardrailTests</c> fails the build when a read path does neither.
/// </summary>
public interface IViewerAccessService
{
    /// <summary>The titles and files the current request may see, after every stage has been applied.</summary>
    ValueTask<ViewerCatalogue> GetCatalogueAsync(CancellationToken ct = default);

    /// <summary>The works the current request may see, one row per work: the same list the shared lists are built from.</summary>
    ValueTask<IReadOnlyList<DisplayWorkRow>> GetVisibleWorksAsync(CancellationToken ct = default);

    /// <summary>The ids of the works the current request may see.</summary>
    ValueTask<IReadOnlySet<Guid>> GetVisibleWorkIdsAsync(CancellationToken ct = default);
}

public sealed class ViewerAccessService(IDisplayProjectionReadService display) : IViewerAccessService
{
    public async ValueTask<ViewerCatalogue> GetCatalogueAsync(CancellationToken ct = default)
    {
        // Every (work, file) row the request may see: the authorized projection has already dropped the Vault, hidden libraries,
        // libraries hidden for its household, items over the content limit and files that are only Unmatched. A request
        // with no usable authority gets no rows, so the catalogue is empty and fails closed.
        var rows = await display.LoadWorkLibraryRowsAsync(ct).ConfigureAwait(false);
        return CatalogueOf(rows);
    }

    /// <summary>
    /// The catalogue for rows the authorized projection already produced. The one place a catalogue is built from rows, so every caller
    /// agrees on what a row means (the work, the file it shows and the library it sits in).
    /// </summary>
    public static ViewerCatalogue CatalogueOf(IEnumerable<DisplayWorkRow> rows) =>
        new(rows.Select(row => new ViewerCatalogueFile(row.WorkId, row.AssetId, row.CreatedAt, row.LibraryId)));

    public async ValueTask<IReadOnlyList<DisplayWorkRow>> GetVisibleWorksAsync(CancellationToken ct = default) =>
        await display.LoadWorksAsync(ct).ConfigureAwait(false);

    public async ValueTask<IReadOnlySet<Guid>> GetVisibleWorkIdsAsync(CancellationToken ct = default) =>
        await display.LoadVisibleWorkIdsAsync(ct).ConfigureAwait(false);
}
