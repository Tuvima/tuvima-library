using System.Text.Json;

namespace MediaEngine.Domain.PersonalMedia;

public enum CollectionViewSourceKind
{
    Gallery,
    SmartRule,
}

/// <summary>What happened when a personal-media source was taken out of a collection that must keep a member.</summary>
public enum CollectionViewSourceRemoval
{
    /// <summary>No such source in the collection.</summary>
    NotFound,

    /// <summary>The source was deleted.</summary>
    Removed,

    /// <summary>The source is the collection's last member (no items, no other source), so it was left in place.</summary>
    WouldLeaveCollectionEmpty,
}

public sealed record ViewSmartRuleDefinition
{
    public const int CurrentVersion = 1;

    private static readonly IReadOnlySet<string> ProhibitedAssetIdentityTerms =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "assetid",
            "assetids",
            "itemid",
            "itemids",
            "localassetid",
            "localassetids",
            "localitemid",
            "localitemids",
        };

    private ViewSmartRuleDefinition(int version, string json)
    {
        Version = version;
        Json = json;
    }

    public int Version { get; }
    public string Json { get; }

    public static ViewSmartRuleDefinition Create(int version, string json)
    {
        if (version != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version), $"Only View smart-rule version {CurrentVersion} is supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("View smart rule must contain valid JSON.", nameof(json), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.EnumerateObject().Any())
            {
                throw new ArgumentException("View smart rule must be a non-empty JSON object.", nameof(json));
            }
            RejectAssetIdentity(document.RootElement);
            return new ViewSmartRuleDefinition(version, document.RootElement.GetRawText());
        }
    }

    private static void RejectAssetIdentity(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsAssetIdentityTerm(property.Name))
                    {
                        throw new ArgumentException("View smart rules cannot select individual personal assets.");
                    }

                    RejectAssetIdentity(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    RejectAssetIdentity(child);
                }

                break;
            case JsonValueKind.String:
                if (IsAssetIdentityTerm(element.GetString() ?? string.Empty))
                {
                    throw new ArgumentException("View smart rules cannot select individual personal assets.");
                }

                break;
        }
    }

    private static bool IsAssetIdentityTerm(string value) =>
        ProhibitedAssetIdentityTerms.Contains(string.Concat(
            value.Where(character => char.IsLetterOrDigit(character))
                .Select(char.ToLowerInvariant)));
}

public sealed record CollectionViewSource(
    Guid Id,
    Guid CollectionId,
    Guid OwnerProfileId,
    CollectionViewSourceKind Kind,
    Guid? GalleryId,
    ViewSmartRuleDefinition? SmartRule,
    int Position,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AddCollectionGallerySourceCommand(
    Guid CollectionId,
    Guid OwnerProfileId,
    Guid GalleryId,
    int Position = 0);

public sealed record AddCollectionViewRuleSourceCommand(
    Guid CollectionId,
    Guid OwnerProfileId,
    ViewSmartRuleDefinition SmartRule,
    int Position = 0);

public sealed record UpdateCollectionViewSourceCommand(
    Guid SourceId,
    Guid CollectionId,
    Guid OwnerProfileId,
    CollectionViewSourceKind Kind,
    Guid? GalleryId,
    ViewSmartRuleDefinition? SmartRule,
    int Position);

/// <summary>
/// Count-free source metadata used by the authorized projection layer. Gallery
/// membership and matching local assets remain dynamic and are never expanded
/// into Collection storage.
/// </summary>
public sealed record CollectionViewSourceProjection(
    Guid SourceId,
    Guid CollectionId,
    Guid OwnerProfileId,
    CollectionViewSourceKind Kind,
    Guid? GalleryId,
    int? RuleVersion,
    string? RuleJson,
    int Position);
