using System.Globalization;
using MediaEngine.Api.Services.Collections.Packs;
using MediaEngine.Contracts.Import;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Services.Import;

/// <summary>
/// The collection pack file kind of the shared Import (Collections and lists spec D14, D17). Recognising a file and the
/// preview in the pack's own words come from the packs packet's <see cref="IPackFileImporter"/>, so the format lives in one
/// place. Importers are scoped services, so each call opens its own scope.
///
/// <para>
/// The dialog does not create the collections itself: creating them is a collections action (<c>Collections write</c>) and the
/// shared apply route is not, so the preview says to add the pack from New collection, Start from a pack, which owns the
/// ownership, audience and Show on choices.
/// </para>
/// </summary>
public sealed class PackFileImportKindHandler(IServiceScopeFactory scopes) : IImportKindHandler
{
    public string Kind => ImportKinds.PackFile;

    public int Priority => 20;

    public bool Detect(ImportSource source)
    {
        if (source.IsZip)
        {
            return false;
        }

        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetServices<IPackFileImporter>()
            .Any(importer => string.Equals(importer.Kind, Kind, StringComparison.Ordinal) && importer.Recognises(source.Text));
    }

    public async Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var importer = scope.ServiceProvider.GetServices<IPackFileImporter>()
            .First(candidate => string.Equals(candidate.Kind, Kind, StringComparison.Ordinal));

        try
        {
            var summary = await importer.PreviewAsync(source.Text, context.ActiveProfile?.Id, ct).ConfigureAwait(false);
            var facts = new List<ImportFactDto>
            {
                new("Collections", summary.Collections.Count.ToString(CultureInfo.InvariantCulture)),
                new("Would create here", summary.WouldCreateCount.ToString(CultureInfo.InvariantCulture)),
            };
            return new ImportPreviewDto
            {
                Kind = Kind,
                KindLabel = "Collection pack",
                Recognised = true,
                Heading = string.IsNullOrWhiteSpace(summary.Name) ? "Collection pack" : summary.Name,
                Subheading = summary.CreatesSet ? "A set of collections" : "A ready-made collection",
                Facts = facts,
                Notes = ["A pack holds names, rules and windows. It holds no user data."],
                Message = "Add it from New collection, Start from a pack.",
                CanApply = false,
            };
        }
        catch (CollectionPackException ex)
        {
            return new ImportPreviewDto
            {
                Kind = Kind,
                KindLabel = "Collection pack",
                Recognised = false,
                Message = ex.Message,
            };
        }
    }

    public Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct) =>
        Task.FromResult(new ImportApplyResultDto(false, "Add collection packs from New collection, Start from a pack."));
}
