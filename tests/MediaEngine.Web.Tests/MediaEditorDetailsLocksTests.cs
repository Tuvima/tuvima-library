using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What the Details tab reads for the locks (editor v5 D5, A3): a value the person changed is their own display override and wins over
/// whatever the provider says now; a field nobody changed shows the provider's current value; their own fields are flagged so they never lock.
/// </summary>
public sealed class MediaEditorDetailsLocksTests
{
    private static readonly string[] EveryScalarKey =
    [
        "title", "original_title", "description", "tagline", "year", "release_date", "air_date", "first_air_date", "last_air_date",
        "publication_date", "original_publication_date", "original_publication_year", "runtime", "duration", "rating", "content_rating",
        "language", "series", "series_position", "disc_number", "track_number", "season_number", "episode_number", "page_count",
        "show_name", "episode_title", "season_title", "issue_title", "episode_description", "issue_description", "album",
    ];

    public static TheoryData<string, string> EveryKindOfItem => new()
    {
        { "Movies", "movie" }, { "Movies", "item" }, { "TV", "series" }, { "TV", "season" }, { "TV", "episode" },
        { "Music", "album" }, { "Music", "track" }, { "Books", "book" }, { "Books", "series" }, { "Audiobooks", "audiobook" },
        { "Audiobooks", "series" }, { "Videos", "series" }, { "Videos", "section" }, { "Videos", "video" }, { "Comics", "issue" },
        { "Comics", "series" },
    };

    [Theory]
    [MemberData(nameof(EveryKindOfItem))]
    public void EveryFieldTheDetailsTabLetsYouChange_IsOneTheEngineKeepsAsYourOwnOverride(string mediaType, string scopeId)
    {
        var scope = Scope(scopeId, overrides: new Dictionary<string, string>(), canonical: EveryScalarKey.ToDictionary(key => key, _ => "5"));

        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { [scopeId] = scope }, mediaType);

        var editable = All(presentation).Where(field => field.CanOverride).ToList();
        Assert.NotEmpty(editable);
        Assert.All(editable, field =>
            Assert.True(DetailDisplayOverrideCatalog.IsAllowed(field.OverrideKey), $"{mediaType}/{scopeId}: '{field.OverrideKey}' is offered for editing but the Engine does not keep it as an override."));
    }

    [Theory]
    [InlineData("title", "Dune", "Dune: Part One", "Dune (my cut)")]
    [InlineData("description", "A family.", "A noble family.", "My own synopsis.")]
    [InlineData("tagline", "Beyond fear.", "Fear is the mind-killer.", "Mine.")]
    [InlineData("release_date", "2021-09-15", "2021-10-22", "2021-12-01")]
    [InlineData("runtime", "150", "155", "140")]
    [InlineData("rating", "7.9", "8.1", "9.5")]
    [InlineData("content_rating", "PG-13", "PG-15", "R")]
    [InlineData("year", "2021", "2022", "2020")]
    public void AChangedField_ShowsThePersonsValue_AfterTheProviderGivesANewOne(string key, string before, string refreshed, string mine)
    {
        Assert.Equal(before, Present(key, canonical: before, overrideValue: null).RawValue);
        var changed = Present(key, canonical: refreshed, overrideValue: mine);
        var untouched = Present(key, canonical: refreshed, overrideValue: null);

        Assert.Equal(mine, changed.RawValue);
        Assert.True(changed.CanRevert);
        Assert.Equal("Override", changed.Provenance);
        Assert.Equal(refreshed, untouched.RawValue);
        Assert.False(untouched.CanRevert);
    }

    [Fact]
    public void ALibraryTagsRow_IsFlaggedAsTheirOwn_AndAProviderFieldIsNot()
    {
        var scope = Scope("movie", overrides: new Dictionary<string, string> { ["custom_tags"] = "epic" },
            canonical: new Dictionary<string, string> { ["title"] = "Dune" });

        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        Assert.True(All(presentation).Single(field => field.Key == "custom_tags").IsOwnField);
        Assert.False(All(presentation).Single(field => field.Key == "title").IsOwnField);
    }

    [Theory]
    [InlineData("custom_tags", true)]
    [InlineData("sort_title", true)]
    [InlineData("sort_name", true)]
    [InlineData("explicit", true)]
    [InlineData("title", false)]
    [InlineData("description", false)]
    [InlineData("genre", false)]
    public void OnlyTheFieldsAPersonFillsThemselves_AreOwnFields(string key, bool own) =>
        Assert.Equal(own, EditorFieldLocks.IsOwnField(key));

    private static MediaEditorDetailsFieldPresentation Present(string key, string canonical, string? overrideValue)
    {
        var overrides = overrideValue is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [key] = overrideValue };
        var scope = Scope("movie", overrides, new Dictionary<string, string> { [key] = canonical });
        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");
        return All(presentation).First(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<MediaEditorDetailsFieldPresentation> All(MediaEditorDetailsPresentation presentation)
    {
        var fields = presentation.Facts.Concat(presentation.Fields).Concat(presentation.AdditionalFields);
        if (presentation.Heading is { } heading)
        {
            fields = fields.Append(heading);
        }

        return presentation.Synopsis is { } synopsis ? fields.Append(synopsis) : fields;
    }

    private static MediaEditorScopeDto Scope(string id, IReadOnlyDictionary<string, string> overrides, IReadOnlyDictionary<string, string> canonical) => new()
    {
        ScopeId = id,
        Label = id,
        CanEditFields = true,
        FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
        {
            AllowedOverrideKeys = [.. DetailDisplayOverrideCatalog.AllowedKeys],
            CanonicalFields = canonical.Select(pair => new MediaEditorScopedFieldValueDto
            {
                Key = pair.Key,
                Value = pair.Value,
                OwnerEntityId = Guid.NewGuid(),
                OwnerEntityKind = "Work",
            }).ToList(),
            DisplayOverrides = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase),
        },
    };
}
