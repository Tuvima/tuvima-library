using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Models;

/// <summary>
/// Provider-neutral ordered container manifest carried through metadata claims
/// until the immediate shelf has been assigned.
/// </summary>
public sealed class ProviderSequenceManifest
{
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("container_id")] public required string ContainerId { get; init; }
    [JsonPropertyName("container_label")] public string? ContainerLabel { get; init; }
    [JsonPropertyName("external_id_key")] public required string ExternalIdKey { get; init; }
    [JsonPropertyName("media_type")] public required string MediaType { get; init; }
    [JsonPropertyName("container_kind")] public string? ContainerKind { get; init; }
    [JsonPropertyName("expected_total")] public int? ExpectedTotal { get; init; }
    [JsonPropertyName("expected_total_kind")] public string? ExpectedTotalKind { get; init; }
    [JsonPropertyName("is_authoritative")] public bool IsAuthoritative { get; init; }
    [JsonPropertyName("items")] public IReadOnlyList<ProviderSequenceManifestItem> Items { get; init; } = [];
}

public sealed class ProviderSequenceManifestItem
{
    [JsonPropertyName("external_id")] public required string ExternalId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("ordinal")] public required string Ordinal { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("duration")] public string? Duration { get; init; }

    /// <summary>The membership scope the provider gives the item (a <c>SeriesMembershipScopeNames</c> value); null means main sequence.</summary>
    [JsonPropertyName("membership_scope")] public string? MembershipScope { get; init; }
}
