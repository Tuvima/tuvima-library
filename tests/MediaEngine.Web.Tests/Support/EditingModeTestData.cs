using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Tests.Support;

/// <summary>The editor context of one movie that the Editing Mode tests load fields, artwork slots and sections from.</summary>
public static class EditingModeTestData
{
    public static readonly Guid MovieId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    public static MediaEditorContextDto MovieContext() => new()
    {
        LaunchEntityId = MovieId,
        MediaType = "Movies",
        InitialScope = "movie",
        AvailableTabs = ["details", "artwork", "links", "history"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = "movie",
                Label = "Movie",
                FieldEntityId = MovieId,
                DisplayTitle = "Dune: Part One",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt", "Background", "Logo"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                {
                    CanonicalFields =
                    [
                        Field("title", "Dune: Part One"),
                        Field("description", "A noble family becomes embroiled in a war for control of the galaxy's most valuable asset."),
                        Field("rating", "8.1"),
                        Field("release_date", "2021-10-22"),
                        Field("runtime", "155"),
                    ],
                    CanonicalArrays =
                    [
                        Array("genre", "Science Fiction", "Adventure"),
                        Array("director", "Denis Villeneuve"),
                    ],
                    DisplayOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["custom_tags"] = "epic" },
                    AllowedOverrideKeys = ["title", "description", "tagline", "genre", "custom_tags", "rating", "release_date", "runtime", "year"],
                },
            },
        ],
    };

    private static MediaEditorScopedFieldValueDto Field(string key, string value) =>
        new() { Key = key, Value = value, ProviderName = "tmdb" };

    private static MediaEditorScopedFieldArrayDto Array(string key, params string[] values) =>
        new()
        {
            Key = key,
            Entries = values.Select((value, index) => new MediaEditorScopedArrayEntryDto { Ordinal = index, Value = value }).ToList(),
        };
}
