using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Import;

namespace MediaEngine.Api.Services.Import;

/// <summary>
/// PLACEHOLDER for the collection pack file kind (Collections and lists spec D14), here so the shared Import already opens the
/// right preview for a pack file. The collection packs packet (WP7) owns the pack file format, its catalogue and applying it:
/// it replaces this class's <see cref="Detect"/>, <see cref="PreviewAsync"/> and <see cref="ApplyAsync"/> in place, or registers
/// its own <see cref="IImportKindHandler"/> for <see cref="ImportKinds.PackFile"/> after this one in
/// <c>AddTuvimaImport</c> (a later registration of the same kind replaces an earlier one).
///
/// <para>
/// Until then a pack file is recognised by a JSON document whose <c>format</c> is <see cref="FormatMarker"/>; the preview reads
/// only its <c>name</c> and says that adding packs arrives with Start from a pack. Nothing is created.
/// </para>
/// </summary>
public sealed class PackFileImportKindHandler : IImportKindHandler
{
    /// <summary>The <c>format</c> value a collection pack file carries.</summary>
    public const string FormatMarker = "tuvima-collection-pack";

    public string Kind => ImportKinds.PackFile;

    public int Priority => 20;

    public bool Detect(ImportSource source) => !source.IsZip && TryReadMarker(source.Text, out _);

    public Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        TryReadMarker(source.Text, out var document);
        var name = document is { } root && root.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
            ? nameValue.GetString()?.Trim()
            : null;
        var count = document is { } body && body.TryGetProperty("collections", out var collections) && collections.ValueKind == JsonValueKind.Array
            ? collections.GetArrayLength()
            : (int?)null;

        var facts = new List<ImportFactDto>();
        if (count is { } collectionCount)
        {
            facts.Add(new ImportFactDto("Collections", collectionCount.ToString(CultureInfo.InvariantCulture)));
        }

        return Task.FromResult(new ImportPreviewDto
        {
            Kind = Kind,
            KindLabel = "Collection pack",
            Recognised = true,
            Heading = string.IsNullOrWhiteSpace(name) ? "Collection pack" : name!,
            Subheading = "A ready-made set of collections",
            Facts = facts,
            Notes = ["A pack holds names, rules and windows. It holds no user data."],
            Message = "Library recognises this as a collection pack. Adding packs arrives with Start from a pack.",
            CanApply = false,
        });
    }

    public Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct) =>
        Task.FromResult(new ImportApplyResultDto(false, "Adding collection packs arrives with Start from a pack."));

    private static bool TryReadMarker(string text, out JsonElement? document)
    {
        document = null;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object
                && parsed.RootElement.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.String
                && string.Equals(format.GetString(), FormatMarker, StringComparison.Ordinal))
            {
                document = parsed.RootElement.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // Text that starts like JSON but is not a pack: another kind (or the list reader) takes it.
            return false;
        }

        return false;
    }
}
