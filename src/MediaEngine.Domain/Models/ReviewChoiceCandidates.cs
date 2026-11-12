using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Models;

/// <summary>
/// One retail answer kept in a Needs Review row's <c>candidates_json</c> when Library found a few possible matches and was not sure
/// which is right (Needs Review redesign D11). The row shows up to three of these as choices; picking one applies it the way the
/// editor's Match does. Stored, not sent over the wire as a type: the Dashboard only sees the choices the Engine builds from it.
/// </summary>
public sealed class RetailChoiceCandidate
{
    /// <summary>The provider's configured name (for example <c>tmdb</c>), as the editor's retail match expects it.</summary>
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>The provider's own id for the data source, needed to apply the match.</summary>
    [JsonPropertyName("provider_id")]
    public Guid ProviderId { get; set; }

    [JsonPropertyName("provider_item_id")]
    public string? ProviderItemId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("creator")]
    public string? Creator { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>The candidate's bridge ids as the retail worker stored them: a JSON object of id type to value.</summary>
    [JsonPropertyName("bridge_ids_json")]
    public string? BridgeIdsJson { get; set; }
}

/// <summary>Picks and stores the retail candidates a Needs Review row offers as choices.</summary>
public static class ReviewChoiceCandidates
{
    /// <summary>The most choices a row ever offers.</summary>
    public const int MaxChoices = 3;

    /// <summary>
    /// The top candidates for an ambiguous retail match: best score first, distinct by provider and item, only those the retail scorer
    /// did not reject (they are at or above the ambiguous threshold), at most <see cref="MaxChoices"/>, and always including the best one.
    /// </summary>
    public static IReadOnlyList<RetailChoiceCandidate> Select(IEnumerable<RetailMatchCandidate> candidates, RetailMatchCandidate? best)
    {
        var picked = new List<RetailMatchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(RetailMatchCandidate candidate)
        {
            // A candidate with no item id cannot be told apart from another, so it is keyed by its own id.
            var key = string.IsNullOrWhiteSpace(candidate.ProviderItemId)
                ? $"{candidate.Id:N}"
                : $"{candidate.ProviderName}\u001f{candidate.ProviderItemId}";
            if (picked.Count < MaxChoices && seen.Add(key))
            {
                picked.Add(candidate);
            }
        }

        if (best is not null)
        {
            Add(best);
        }

        foreach (var candidate in candidates
                     .Where(c => !string.Equals(c.Outcome, "Rejected", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(c => c.ScoreTotal))
        {
            Add(candidate);
        }

        return picked
            .OrderByDescending(c => c.ScoreTotal)
            .Select(c => new RetailChoiceCandidate
            {
                ProviderName = c.ProviderName,
                ProviderId = c.ProviderId,
                ProviderItemId = c.ProviderItemId,
                Title = c.Title,
                Creator = c.Creator,
                Year = c.Year,
                ImageUrl = c.ImageUrl,
                Score = c.ScoreTotal,
                BridgeIdsJson = c.BridgeIdsJson,
            })
            .ToList();
    }

    /// <summary>The JSON stored in <c>review_queue.candidates_json</c>.</summary>
    public static string ToJson(IReadOnlyList<RetailChoiceCandidate> candidates) => JsonSerializer.Serialize(candidates);

    /// <summary>Convenience for the retail workers: the stored JSON for the top candidates, or null when there are none to keep.</summary>
    public static string? ToJson(IEnumerable<RetailMatchCandidate> candidates, RetailMatchCandidate? best)
    {
        var picked = Select(candidates, best);
        return picked.Count == 0 ? null : ToJson(picked);
    }
}
