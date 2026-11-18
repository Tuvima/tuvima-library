using System.Globalization;
using Dapper;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// What the universal editor needs to know about a Series beyond its fields (spec <c>universal-editor-v8</c>, D1 and D6): the header's
/// Retail and Canonical badge, the kinds of title the Series holds (the header's first dropdown) and one kind's titles, searched and paged
/// (the second dropdown). The Dashboard only draws what is read here. Titles the person does not own are listed only when the profile's own
/// "Show missing" tick for this Series is on, by the same key rule the Series panel and the Universe page use
/// (<see cref="SeriesMissingItemVisibility"/>). It writes nothing.
/// </summary>
public sealed class EditorSeriesReadService(
    ICollectionRepository collections,
    IDatabaseConnection db,
    ISeriesManifestRepository manifests,
    IProfileSequencePreferencesRepository preferences,
    EditorProviderNames? providerNames = null)
{
    /// <summary>How many titles one page of the second dropdown holds; a Series of hundreds is paged and searched, never loaded whole.</summary>
    public const int PageSize = 50;

    /// <summary>The media types in the order the first dropdown lists them.</summary>
    private static readonly MediaType[] Order =
    [
        MediaType.Books, MediaType.Audiobooks, MediaType.Comics, MediaType.Movies, MediaType.TV, MediaType.Music,
    ];

    /// <summary>The words the first dropdown shows for a media type.</summary>
    public static string LabelFor(MediaType type) => type switch
    {
        MediaType.TV => "TV",
        _ => type.ToString(),
    };

    /// <summary>The badge's two halves: every owned member matched to a provider (retail), and the Series' own Wikidata link (canonical).</summary>
    public async Task<EditorSubjectMatchBadgeDto> ReadBadgeAsync(Guid collectionId, string? wikidataQid, CancellationToken ct = default)
    {
        var owned = await OwnedWorksAsync(collectionId, ct).ConfigureAwait(false);
        var retailMatched = false;
        string? provider = null;
        if (owned.Count > 0)
        {
            using var connection = db.CreateConnection();
            var kinds = connection.Query<WorkKindRow>(
                "SELECT id AS Id, work_kind AS Kind FROM works WHERE id IN @ids;",
                new { ids = owned.Select(work => GuidSql.ToBlob(work.Id)).ToArray() })
                .ToDictionary(row => row.Id, row => row.Kind ?? string.Empty);
            var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allMatched = true;
            foreach (var group in owned.GroupBy(work => work.MediaType))
            {
                var items = group.Select(work => new EditorProviderMatchReader.Item(work.Id, kinds.GetValueOrDefault(work.Id) ?? string.Empty)).ToList();
                var sources = EditorProviderMatchReader.ReadSources(connection, group.Key.ToString(), Guid.Empty, items);
                foreach (var item in items)
                {
                    if (sources.GetValueOrDefault(item.WorkId).Source is { } source)
                    {
                        providers.Add(providerNames?.ProductName(source.ProviderId) ?? source.ProviderName);
                    }
                    else
                    {
                        allMatched = false;
                    }
                }
            }

            retailMatched = allMatched;
            provider = retailMatched && providers.Count == 1 ? providers.First() : null;
        }

        return new EditorSubjectMatchBadgeDto
        {
            RetailMatched = retailMatched,
            ProviderName = provider,
            CanonicalMatched = !string.IsNullOrWhiteSpace(wikidataQid),
        };
    }

    /// <summary>The kinds of title the Series' owner has, in the library's usual order. A kind with none owned is not offered.</summary>
    public async Task<List<EditorSeriesMediaTypeDto>> ReadMediaTypesAsync(Guid collectionId, CancellationToken ct = default)
    {
        var owned = await OwnedWorksAsync(collectionId, ct).ConfigureAwait(false);
        return
        [
            .. owned.GroupBy(work => work.MediaType)
                .OrderBy(group => RankOf(group.Key))
                .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal)
                .Select(group => new EditorSeriesMediaTypeDto { Key = group.Key.ToString(), Label = LabelFor(group.Key), OwnedCount = group.Count() }),
        ];
    }

    /// <summary>
    /// One page of the titles of one media type in the Series, in the Series' order, searched by <paramref name="query"/>. Null when the
    /// Series is gone or the media type is not one.
    /// </summary>
    public async Task<EditorSeriesTitlesDto?> GetTitlesAsync(
        Guid collectionId, string mediaType, string? query, int offset, Guid? profileId, CancellationToken ct = default)
    {
        var type = MediaTypeClassifier.Classify(mediaType);
        if (type == MediaType.Unknown)
        {
            return null;
        }

        var collection = await collections.GetCollectionWithWorksAsync(collectionId, ct).ConfigureAwait(false);
        if (collection is null)
        {
            return null;
        }

        var state = ReadWorkState(collectionId);
        var rows = new List<Row>();
        var coveredWorks = new HashSet<Guid>();
        foreach (var work in collection.Works.Where(work => work.ParentWorkId is null && work.MediaType == type && state.ContainsKey(work.Id)))
        {
            coveredWorks.Add(work.Id);
            if (string.IsNullOrWhiteSpace(TitleOf(work)))
            {
                continue;
            }

            rows.Add(new Row(
                work.Id, TitleOf(work), work.Ordinal, work.Ordinal?.ToString(CultureInfo.InvariantCulture), Owned: !state[work.Id]));
        }

        var manifest = await manifests.GetViewByCollectionIdAsync(collectionId, ct).ConfigureAwait(false);
        var unownedInManifest = new List<Row>();
        if (manifest is not null)
        {
            foreach (var item in manifest.Items.Where(item => !item.IsCollection
                && string.Equals(item.OwnershipState, "Missing", StringComparison.OrdinalIgnoreCase)
                && (item.LinkedWorkId is null || !coveredWorks.Contains(item.LinkedWorkId.Value))
                && MediaTypeClassifier.Classify(item.MediaType ?? item.MediaKind) == type))
            {
                var ordinal = item.ParsedOrdinal ?? item.SortOrder;
                unownedInManifest.Add(new Row(
                    null, string.IsNullOrWhiteSpace(item.ItemLabel) ? item.ItemQid : item.ItemLabel!, null,
                    item.RawOrdinal ?? ordinal?.ToString("0.##", CultureInfo.InvariantCulture), Owned: false, Position: ordinal));
            }
        }

        var showMissing = await ShowMissingAsync(collection, manifest?.SeriesQid, type, profileId, ct).ConfigureAwait(false);
        IEnumerable<Row> all = rows.Where(row => row.Owned);
        if (showMissing)
        {
            all = all.Concat(rows.Where(row => !row.Owned)).Concat(unownedInManifest);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            all = all.Where(row => row.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        var ordered = all
            .OrderBy(row => row.Position ?? row.Ordinal ?? double.MaxValue)
            .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var start = Math.Clamp(offset, 0, ordered.Count);
        return new EditorSeriesTitlesDto
        {
            MediaType = type.ToString(),
            Total = ordered.Count,
            Offset = start,
            ShowingMissing = showMissing,
            Items =
            [
                .. ordered.Skip(start).Take(PageSize).Select(row => new EditorSeriesTitleDto
                {
                    Id = row.Owned ? row.Id : null,
                    Title = row.Title,
                    Number = row.Number,
                    Owned = row.Owned,
                }),
            ],
        };
    }

    // The profile's own tick for this Series and media type, under the Series' collection id then its Wikidata id. A Series starts unticked,
    // so with no tick stored (or no profile) its missing titles stay out of the list.
    private async Task<bool> ShowMissingAsync(Collection collection, string? manifestQid, MediaType type, Guid? profileId, CancellationToken ct)
    {
        var key = SeriesMissingItemVisibility.PolicyKey(type);
        if (key is null || profileId is not { } profile)
        {
            return false;
        }

        foreach (var containerKey in SeriesMissingItemVisibility.OverrideKeys(collection.Id, manifestQid ?? collection.WikidataQid))
        {
            var stored = await preferences.GetAsync(profile, key, containerKey, ct).ConfigureAwait(false);
            if (stored is not null)
            {
                return stored.ShowMissing;
            }
        }

        return false;
    }

    private async Task<List<Work>> OwnedWorksAsync(Guid collectionId, CancellationToken ct)
    {
        var full = await collections.GetCollectionWithWorksAsync(collectionId, ct).ConfigureAwait(false);
        var state = ReadWorkState(collectionId);
        return [.. (full?.Works ?? []).Where(work => work.ParentWorkId is null && work.MediaType != MediaType.Unknown
            && state.TryGetValue(work.Id, out var catalogOnly) && !catalogOnly)];
    }

    /// <summary>
    /// The Series' works the library shows, each with whether it is only a catalogue entry (not owned). A work in the Vault is left out, so the
    /// header dropdowns never name one. The collection read does not carry the catalogue flag, so it is read here.
    /// </summary>
    private Dictionary<Guid, bool> ReadWorkState(Guid collectionId)
    {
        using var connection = db.CreateConnection();
        var state = new Dictionary<Guid, bool>();
        foreach (var catalogOnly in new[] { false, true })
        {
            var ids = connection.Query<Guid>(
                $"SELECT id FROM works WHERE collection_id = @collection AND COALESCE(is_catalog_only, 0) = @flag AND {VaultSql.NotVaultEntity("works.id")};",
                new { collection = GuidSql.ToBlob(collectionId), flag = catalogOnly ? 1 : 0 });
            foreach (var id in ids)
            {
                state[id] = catalogOnly;
            }
        }

        return state;
    }

    private static int RankOf(MediaType type)
    {
        var index = Array.IndexOf(Order, type);
        return index < 0 ? Order.Length : index;
    }

    private static string TitleOf(Work work) =>
        work.CanonicalValues
            .Where(value => string.Equals(value.Key, "title", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Value)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim() ?? string.Empty;

    private sealed record Row(Guid? Id, string Title, int? Ordinal, string? Number, bool Owned, double? Position = null);

    private sealed record WorkKindRow(Guid Id, string? Kind);
}
