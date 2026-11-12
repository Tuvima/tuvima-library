using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// One answer a Needs Review row can offer (Needs Review redesign D11), read from the row's stored candidates. The list turns it into a
/// <see cref="NeedsReviewChoiceDto"/>; <c>POST /review/{id}/choose</c> reads the same options again and applies the one whose id matches,
/// so a person can only ever apply something the list offered for that row.
/// </summary>
internal sealed record ReviewChoiceOption(
    string Id,
    string Kind,
    string Title,
    string? Subtitle,
    string? ImageUrl,
    string? ImageProvider,
    RetailChoiceCandidate? Retail = null,
    Guid? CollectionId = null,
    MediaType? MediaType = null)
{
    public NeedsReviewChoiceDto ToDto() => new() { Id = Id, Kind = Kind, Title = Title, Subtitle = Subtitle };
}

/// <summary>The one place that turns a review row's stored candidates into the choices it offers.</summary>
internal static class NeedsReviewChoices
{
    /// <summary>
    /// The choices a row offers: for an ambiguous retail match, the stored matches (only for a kind of item a retail match can be applied
    /// to); for two Series, the Series; for an ambiguous file type, the types. Null, empty or unreadable candidates offer none.
    /// </summary>
    public static IReadOnlyList<ReviewChoiceOption> Read(string? trigger, string? candidatesJson, string? mediaType, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson) || trigger is null)
        {
            return [];
        }

        try
        {
            if (string.Equals(trigger, ReviewTrigger.RetailMatchAmbiguous, StringComparison.Ordinal))
            {
                return ReadRetail(candidatesJson, mediaType);
            }

            if (string.Equals(trigger, ReviewTrigger.AmbiguousSeriesMembership, StringComparison.Ordinal))
            {
                return ReadSeries(candidatesJson);
            }

            if (string.Equals(trigger, ReviewTrigger.AmbiguousMediaType, StringComparison.Ordinal))
            {
                return ReadMediaTypes(candidatesJson);
            }
        }
        catch (JsonException ex)
        {
            // Candidates that cannot be read simply offer no choices; the row still lists and Fix still works.
            logger?.LogDebug(ex, "Needs Review candidates for trigger {Trigger} could not be read; the row offers no choices", trigger);
        }

        return [];
    }

    /// <summary>
    /// The editor's Match target an ambiguous retail match is applied to, for the kind of item the row is about; null when a retail match
    /// cannot be applied to it from here (television needs a TheTVDB show and episode chosen in the editor).
    /// </summary>
    public static (string TargetKind, string FieldGroup)? RetailTargetFor(string? mediaType) =>
        MediaTypeParser.Parse(mediaType ?? string.Empty) switch
        {
            MediaType.Movies => ("item", "movie_identity"),
            MediaType.Books => ("item", "book_identity"),
            MediaType.Audiobooks => ("item", "audiobook_identity"),
            MediaType.Comics => ("item", "issue"),
            MediaType.Music => ("item", "track"),
            _ => null,
        };

    /// <summary>The plain name of a library type, as the rest of the app names it.</summary>
    public static string DisplayName(MediaType type) => type switch
    {
        MediaType.Movies => "Movies",
        MediaType.TV => "TV Shows",
        MediaType.Music => "Music",
        MediaType.Books => "Books",
        MediaType.Audiobooks => "Audiobooks",
        MediaType.Comics => "Comics",
        MediaType.Videos => "Videos",
        _ => type.ToString(),
    };

    private static IReadOnlyList<ReviewChoiceOption> ReadRetail(string candidatesJson, string? mediaType)
    {
        if (RetailTargetFor(mediaType) is null)
        {
            return [];
        }

        var stored = JsonSerializer.Deserialize<List<RetailChoiceCandidate>>(candidatesJson) ?? [];
        var options = new List<ReviewChoiceOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in stored)
        {
            // Without a title, an item id and a provider the match could not be shown or applied.
            if (candidate is null
                || string.IsNullOrWhiteSpace(candidate.Title)
                || string.IsNullOrWhiteSpace(candidate.ProviderItemId)
                || string.IsNullOrWhiteSpace(candidate.ProviderName)
                || candidate.ProviderId == Guid.Empty)
            {
                continue;
            }

            var id = $"retail:{candidate.ProviderName}:{candidate.ProviderItemId}";
            if (!seen.Add(id))
            {
                continue;
            }

            var year = YearOf(candidate.Year);
            options.Add(new ReviewChoiceOption(
                id,
                NeedsReviewChoiceKinds.Retail,
                year is null ? candidate.Title.Trim() : $"{candidate.Title.Trim()} ({year})",
                string.IsNullOrWhiteSpace(candidate.Creator) ? null : candidate.Creator.Trim(),
                candidate.ImageUrl,
                candidate.ProviderName,
                Retail: candidate));
            if (options.Count == ReviewChoiceCandidates.MaxChoices)
            {
                break;
            }
        }

        return options;
    }

    private static IReadOnlyList<ReviewChoiceOption> ReadSeries(string candidatesJson)
    {
        var stored = JsonSerializer.Deserialize<List<StoredSeries>>(candidatesJson) ?? [];
        var options = new List<ReviewChoiceOption>();
        var seen = new HashSet<Guid>();
        foreach (var series in stored)
        {
            var label = !string.IsNullOrWhiteSpace(series?.SeriesLabel) ? series.SeriesLabel : series?.SeriesQid;
            if (series?.CollectionId is not { } collectionId || collectionId == Guid.Empty
                || string.IsNullOrWhiteSpace(label) || !seen.Add(collectionId))
            {
                continue;
            }

            options.Add(new ReviewChoiceOption(
                $"series:{collectionId:N}", NeedsReviewChoiceKinds.Series, label.Trim(), null, null, null, CollectionId: collectionId));
            if (options.Count == ReviewChoiceCandidates.MaxChoices)
            {
                break;
            }
        }

        return options;
    }

    private static IReadOnlyList<ReviewChoiceOption> ReadMediaTypes(string candidatesJson)
    {
        var stored = JsonSerializer.Deserialize<List<StoredMediaType>>(candidatesJson) ?? [];
        var options = new List<ReviewChoiceOption>();
        var seen = new HashSet<MediaType>();
        foreach (var candidate in stored)
        {
            var type = MediaTypeParser.Parse(candidate?.Type ?? string.Empty);
            if (type == MediaType.Unknown || !seen.Add(type))
            {
                continue;
            }

            options.Add(new ReviewChoiceOption(
                $"media_type:{type}", NeedsReviewChoiceKinds.MediaType, DisplayName(type), null, null, null, MediaType: type));
            if (options.Count == ReviewChoiceCandidates.MaxChoices)
            {
                break;
            }
        }

        return options;
    }

    // "2021" from "2021" or "2021-09-15"; anything that does not start with a four-digit year is shown as it is.
    private static string? YearOf(string? year)
    {
        if (string.IsNullOrWhiteSpace(year))
        {
            return null;
        }

        var trimmed = year.Trim();
        return trimmed.Length >= 4 && int.TryParse(trimmed.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? trimmed[..4]
            : trimmed;
    }

    private sealed class StoredSeries
    {
        [JsonPropertyName("series_qid")] public string? SeriesQid { get; set; }
        [JsonPropertyName("series_label")] public string? SeriesLabel { get; set; }
        [JsonPropertyName("collection_id")] public Guid? CollectionId { get; set; }
    }

    private sealed class StoredMediaType
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}
