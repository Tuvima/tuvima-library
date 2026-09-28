using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Helpers;

/// <summary>
/// Extracts person references from metadata claims and canonical values.
/// Static helper with no dependencies — used by pipeline workers and enrichment workers.
/// </summary>
public static class PersonReferenceExtractor
{
    /// <summary>
    /// Extracts person references directly from raw <see cref="ProviderClaim"/> lists.
    /// Returns only references with a confirmed Wikidata QID.
    /// Handles collective pseudonyms and constituent members.
    /// </summary>
    public static IReadOnlyList<PersonReference> FromRawClaims(
        IReadOnlyList<ProviderClaim> rawClaims,
        MediaType mediaType = MediaType.Unknown)
    {
        var byKey = AccumulateByKey(rawClaims);
        // Album-artist tags are also common in audiobook containers where they
        // identify the narrator. They must not switch an audiobook into the
        // music/Performer path or create a second unsupported contributor role.
        var splitMusicCredit = mediaType == MediaType.Music
            || (mediaType == MediaType.Unknown && byKey.ContainsKey("album_artist"));
        var performerRole = splitMusicCredit ? "Performer" : ResolvePerformerRole(mediaType);

        var refs = new List<PersonReference>();
        // Generic audio tag aliases must not turn music performers/composers into
        // book authors or audiobook narrators (and select unrelated namesakes).
        if (!splitMusicCredit)
        {
            AddPersonRefsFromLists(refs, "Author", byKey, MetadataFieldConstants.Author, "author_qid");
            AddPersonRefsFromLists(refs, "Narrator", byKey, MetadataFieldConstants.Narrator, "narrator_qid");
        }
        AddPersonRefsFromLists(refs, performerRole, byKey, "performer", "performer_qid");
        AddPersonRefsFromLists(refs, performerRole, byKey, MetadataFieldConstants.Artist, "artist_qid", splitMusicCredit);
        AddPersonRefsFromLists(refs, performerRole, byKey, "album_artist", "album_artist_qid", splitMusicCredit);
        AddPersonRefsFromLists(refs, "Director", byKey, "director", "director_qid");
        AddPersonRefsFromLists(refs, "Screenwriter", byKey, "screenwriter", "screenwriter_qid");
        AddPersonRefsFromLists(refs, "Composer", byKey, "composer", "composer_qid");
        AddPersonRefsFromLists(refs, "Producer", byKey, "producer", "producer_qid");
        AddPersonRefsFromLists(refs, "Actor", byKey, "cast_member", "cast_member_qid");
        AddPersonRefsFromLists(refs, "Actor", byKey, MetadataFieldConstants.GuestStar, "guest_star_qid");

        // Mark author refs as collective pseudonyms when the adapter flagged it.
        if (byKey.TryGetValue("author_is_collective_pseudonym", out var pseudoFlags)
            && pseudoFlags.Any(f => string.Equals(f, "true", StringComparison.OrdinalIgnoreCase)))
        {
            for (int i = 0; i < refs.Count; i++)
            {
                if (string.Equals(refs[i].Role, "Author", StringComparison.OrdinalIgnoreCase))
                {
                    refs[i] = refs[i] with { IsCollectivePseudonym = true };
                }
            }
        }

        // QID-first: only emit references with a confirmed Wikidata QID.
        return CollapseEquivalentRoles(refs
            .Where(r => !string.IsNullOrEmpty(r.WikidataQid))
            .GroupBy(r => $"{r.WikidataQid}::{r.Role}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList());
    }

    /// <summary>
    /// Extracts person references that have a name but NO Wikidata QID.
    /// These are candidates for standalone person reconciliation.
    /// </summary>
    public static IReadOnlyList<PersonReference> FromRawClaimsUnlinked(
        IReadOnlyList<ProviderClaim> rawClaims,
        MediaType mediaType = MediaType.Unknown)
    {
        var byKey = AccumulateByKey(rawClaims);
        var splitMusicCredit = mediaType == MediaType.Music
            || (mediaType == MediaType.Unknown && byKey.ContainsKey("album_artist"));
        var performerRole = splitMusicCredit ? "Performer" : ResolvePerformerRole(mediaType);

        var refs = new List<PersonReference>();
        // Generic audio tag aliases must not turn music performers/composers into
        // book authors or audiobook narrators (and select unrelated namesakes).
        if (!splitMusicCredit)
        {
            AddPersonRefsFromLists(refs, "Author", byKey, MetadataFieldConstants.Author, "author_qid");
            AddPersonRefsFromLists(refs, "Narrator", byKey, MetadataFieldConstants.Narrator, "narrator_qid");
        }
        AddPersonRefsFromLists(refs, performerRole, byKey, "performer", "performer_qid");
        AddPersonRefsFromLists(refs, performerRole, byKey, MetadataFieldConstants.Artist, "artist_qid", splitMusicCredit);
        AddPersonRefsFromLists(refs, performerRole, byKey, "album_artist", "album_artist_qid", splitMusicCredit);
        AddPersonRefsFromLists(refs, "Director", byKey, "director", "director_qid");
        AddPersonRefsFromLists(refs, "Screenwriter", byKey, "screenwriter", "screenwriter_qid");
        AddPersonRefsFromLists(refs, "Composer", byKey, "composer", "composer_qid");
        AddPersonRefsFromLists(refs, "Producer", byKey, "producer", "producer_qid");
        AddPersonRefsFromLists(refs, "Actor", byKey, "cast_member", "cast_member_qid");
        AddPersonRefsFromLists(refs, "Actor", byKey, MetadataFieldConstants.GuestStar, "guest_star_qid");

        return refs
            .Where(r => string.IsNullOrEmpty(r.WikidataQid) && !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => $"{r.Role}::{r.Name}".ToUpperInvariant())
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Extracts person references from canonical values (post-scoring).
    /// When a <c>*_qid</c> companion canonical is present, each QID::Label pair
    /// is parsed and the Wikidata QID is forwarded to the PersonReference.
    /// </summary>
    public static IReadOnlyList<PersonReference> FromCanonicals(
        IReadOnlyList<CanonicalValue> canonicals,
        MediaType mediaType = MediaType.Unknown)
        => [];

    /// <summary>
    /// Extracts person references from canonical array rows. Multi-valued person
    /// metadata is stored here, not in scalar canonical values.
    /// </summary>
    public static IReadOnlyList<PersonReference> FromCanonicalArrays(
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> arrays,
        MediaType mediaType = MediaType.Unknown)
    {
        var splitMusicCredit = mediaType == MediaType.Music
            || (mediaType == MediaType.Unknown && arrays.ContainsKey("album_artist"));
        var performerRole = splitMusicCredit ? "Performer" : ResolvePerformerRole(mediaType);

        var refs = new List<PersonReference>();
        // Generic audio tag aliases must not turn music performers/composers into
        // book authors or audiobook narrators (and select unrelated namesakes).
        if (!splitMusicCredit)
        {
            AddPersonRefsFromArrays(refs, "Author", arrays, MetadataFieldConstants.Author, "author_qid");
            AddPersonRefsFromArrays(refs, "Narrator", arrays, MetadataFieldConstants.Narrator, "narrator_qid");
        }
        AddPersonRefsFromArrays(refs, performerRole, arrays, "performer", "performer_qid");
        AddPersonRefsFromArrays(refs, performerRole, arrays, MetadataFieldConstants.Artist, "artist_qid", splitMusicCredit);
        AddPersonRefsFromArrays(refs, performerRole, arrays, "album_artist", "album_artist_qid", splitMusicCredit);
        AddPersonRefsFromArrays(refs, "Director", arrays, "director", "director_qid");
        AddPersonRefsFromArrays(refs, "Screenwriter", arrays, "screenwriter", "screenwriter_qid");
        AddPersonRefsFromArrays(refs, "Composer", arrays, "composer", "composer_qid");
        AddPersonRefsFromArrays(refs, "Producer", arrays, "producer", "producer_qid");
        AddPersonRefsFromArrays(refs, "Actor", arrays, "cast_member", "cast_member_qid");
        AddPersonRefsFromArrays(refs, "Actor", arrays, MetadataFieldConstants.GuestStar, "guest_star_qid");

        return CollapseEquivalentRoles(refs
            .GroupBy(r => $"{r.Role}::{r.WikidataQid ?? r.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList());
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string ResolvePerformerRole(MediaType mediaType) => mediaType switch
    {
        MediaType.Music => "Performer",
        MediaType.Audiobooks => "Narrator",
        _ => "Actor",
    };

    private static Dictionary<string, List<string>> AccumulateByKey(IReadOnlyList<ProviderClaim> claims)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in claims)
        {
            if (!byKey.TryGetValue(c.Key, out var list))
            {
                list = [];
                byKey[c.Key] = list;
            }
            list.Add(c.Value);
        }
        return byKey;
    }

    private static void AddPersonRefsFromLists(
        List<PersonReference> refs,
        string role,
        Dictionary<string, List<string>> byKey,
        string nameKey,
        string qidKey,
        bool splitMusicCredit = false)
    {
        if (!byKey.TryGetValue(nameKey, out var storedNames))
        {
            return;
        }

        var names = ExpandJoinedPersonValues(storedNames, splitMusicCredit);
        byKey.TryGetValue(qidKey, out var storedQids);
        var qids = ExpandJoinedPersonValues(storedQids);

        var maxCount = Math.Max(names.Count, qids?.Count ?? 0);
        for (int i = 0; i < maxCount; i++)
        {
            var name = i < names.Count ? names[i] : null;
            string? qid = null;
            string? qidLabel = null;
            if (qids is not null && i < qids.Count)
            {
                var segment = qids[i];
                var colonIdx = segment.IndexOf("::", StringComparison.Ordinal);
                if (colonIdx > 0)
                {
                    qid = segment[..colonIdx].Trim();
                    qidLabel = segment[(colonIdx + 2)..].Trim();
                }
                else if (!string.IsNullOrWhiteSpace(segment))
                {
                    qid = segment.Trim();
                }
            }

            var displayName = StringHelpers.FirstNonBlank(qidLabel, name, qid)?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            refs.Add(new PersonReference(role, displayName, string.IsNullOrEmpty(qid) ? null : qid));
        }
    }

    private static IReadOnlyList<string> ExpandJoinedPersonValues(
        IReadOnlyList<string>? values,
        bool splitMusicCredit = false)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        return values
            .SelectMany(value => SplitPersonValue(value, splitMusicCredit))
            .ToList();
    }

    private static IReadOnlyList<string> SplitPersonValue(string value, bool splitMusicCredit)
    {
        var semicolonParts = value.Split(
            ';',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!splitMusicCredit)
        {
            return semicolonParts;
        }

        var result = new List<string>();
        foreach (var part in semicolonParts)
        {
            var commaParts = part.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            // MusicBrainz joins distinct artist-credit entries with their
            // provider join phrase. A comma is a contributor delimiter only
            // when every segment looks like a complete multi-token name. This
            // keeps names such as "Batiste, Jon" and groups such as
            // "Earth, Wind & Fire" intact while separating credits such as
            // "Jon Batiste, Ludwig van Beethoven".
            if (commaParts.Length is >= 2 and <= 8
                && commaParts.All(segment => segment.Contains(' ')))
            {
                result.AddRange(commaParts);
            }
            else
            {
                result.Add(part);
            }
        }

        return result;
    }

    private static void AddPersonRefsFromArrays(
        List<PersonReference> refs,
        string role,
        IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> arrays,
        string nameKey,
        string qidKey,
        bool splitMusicCredit = false)
    {
        if (!arrays.TryGetValue(nameKey, out var names) || names.Count == 0)
        {
            return;
        }

        arrays.TryGetValue(qidKey, out var qidEntries);
        if (splitMusicCredit && qidEntries is null)
        {
            names = names
                .SelectMany(entry => SplitPersonValue(entry.Value, splitMusicCredit)
                    .Select((name, index) => new CanonicalArrayEntry
                    {
                        Ordinal = (entry.Ordinal * 100) + index,
                        Value = name,
                    }))
                .OrderBy(entry => entry.Ordinal)
                .ToList();
        }

        var maxCount = Math.Max(names.Count, qidEntries?.Count ?? 0);
        for (int i = 0; i < maxCount; i++)
        {
            var nameEntry = i < names.Count ? names[i] : null;
            var name = nameEntry?.Value;
            string? qid = null;
            string? qidLabel = null;

            if (!string.IsNullOrWhiteSpace(nameEntry?.ValueQid))
            {
                qid = nameEntry.ValueQid.Trim();
            }

            if (qidEntries is not null && i < qidEntries.Count)
            {
                var segment = StringHelpers.FirstNonBlank(qidEntries[i].ValueQid, qidEntries[i].Value)?.Trim();
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    var colonIdx = segment.IndexOf("::", StringComparison.Ordinal);
                    if (colonIdx > 0)
                    {
                        qid = segment[..colonIdx].Trim();
                        qidLabel = segment[(colonIdx + 2)..].Trim();
                    }
                    else
                    {
                        qid = segment.Trim();
                    }
                }
            }

            var displayName = StringHelpers.FirstNonBlank(qidLabel, name, qid)?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            refs.Add(new PersonReference(role, displayName, string.IsNullOrEmpty(qid) ? null : qid));
        }
    }

    private static IReadOnlyList<PersonReference> CollapseEquivalentRoles(IReadOnlyList<PersonReference> refs)
    {
        var result = refs.ToList();
        var narratorQids = result
            .Where(r => string.Equals(r.Role, "Narrator", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(r.WikidataQid))
            .Select(r => r.WikidataQid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (narratorQids.Count == 0)
        {
            return result;
        }

        return result
            .Where(r => !(string.Equals(r.Role, "Actor", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(r.WikidataQid)
                && narratorQids.Contains(r.WikidataQid!)))
            .ToList();
    }
}
