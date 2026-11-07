using MediaEngine.Contracts.Editing;
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

    [Fact]
    public void AnOverriddenField_CarriesTheProvidersCurrentValue_ForRevertToShow_AndAnUntouchedOneDoesNot()
    {
        var scope = Scope("movie", new Dictionary<string, string> { ["title"] = "Dune (my cut)" },
            new Dictionary<string, string> { ["title"] = "Dune: Part One", ["tagline"] = "Beyond fear." });

        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        var title = All(presentation).Single(field => field.Key == "title");
        Assert.Equal("Dune (my cut)", title.RawValue);
        Assert.Equal("Dune: Part One", title.ProviderValue);
        Assert.Null(All(presentation).Single(field => field.Key == "tagline").ProviderValue);
    }

    [Fact]
    public void AnOverriddenList_CarriesTheProvidersCurrentList()
    {
        var scope = Scope("movie", new Dictionary<string, string> { ["genre"] = "Folk; Nordic" }, new Dictionary<string, string> { ["title"] = "Dune" });
        scope.FieldSnapshot.CanonicalArrays =
        [
            new MediaEditorScopedFieldArrayDto
            {
                Key = "genre",
                OwnerEntityId = Guid.NewGuid(),
                OwnerEntityKind = "Work",
                Entries = [new MediaEditorScopedArrayEntryDto { Ordinal = 0, Value = "Drama" }, new MediaEditorScopedArrayEntryDto { Ordinal = 1, Value = "War" }],
            },
        ];

        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        var genres = All(presentation).Single(field => field.Key == "genre");
        Assert.Equal("Folk; Nordic", genres.RawValue);
        Assert.Equal("Drama, War", genres.ProviderValue);
    }

    [Theory]
    [InlineData("Movies", "movie")]
    [InlineData("Books", "book")]
    [InlineData("TV", "series")]
    [InlineData("Music", "album")]
    public void TheSortTitle_IsOffered_EvenWhenBlank_AsTheirOwnField(string mediaType, string scopeId)
    {
        var scope = Scope(scopeId, new Dictionary<string, string>(), new Dictionary<string, string> { ["title"] = "The Dune Chronicles", ["album"] = "The Dune Chronicles", ["show_name"] = "The Dune Chronicles" });

        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { [scopeId] = scope }, mediaType);

        var sortTitle = All(presentation).Single(field => field.Key == "sort_title");
        Assert.Equal("Sort title", sortTitle.Label);
        Assert.Equal(string.Empty, sortTitle.RawValue);
        Assert.True(sortTitle.CanOverride);
        Assert.False(sortTitle.CanRevert);
        Assert.True(sortTitle.IsOwnField);
    }

    [Fact]
    public void ASavedSortTitle_ReadsBackAsTheirValue_WithRevert()
    {
        var scope = Scope("movie", new Dictionary<string, string> { ["sort_title"] = "Dune Chronicles, The" },
            new Dictionary<string, string> { ["title"] = "The Dune Chronicles" });

        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        var sortTitle = All(presentation).Single(field => field.Key == "sort_title");
        Assert.Equal("Dune Chronicles, The", sortTitle.RawValue);
        Assert.True(sortTitle.CanRevert);
        Assert.True(sortTitle.IsOwnField);
    }

    [Fact]
    public void TheSortTitle_IsNotOffered_WhereTheItemCannotBeEdited_OrTheEngineDoesNotAllowIt()
    {
        var readOnly = Scope("movie", new Dictionary<string, string>(), new Dictionary<string, string> { ["title"] = "Dune" });
        readOnly.CanEditFields = false;
        var notAllowed = Scope("movie", new Dictionary<string, string>(), new Dictionary<string, string> { ["title"] = "Dune" });
        notAllowed.FieldSnapshot.AllowedOverrideKeys.Remove("sort_title");

        foreach (var scope in new[] { readOnly, notAllowed })
        {
            var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");
            Assert.DoesNotContain(All(presentation), field => field.Key == "sort_title");
        }
    }

    [Fact]
    public void TheSortTitleTheDetailsTabOffers_IsOneTheEngineAcceptsAsADisplayOverride() =>
        Assert.True(DetailDisplayOverrideCatalog.TryValidateValue("sort_title", "Dune Chronicles, The", out _, out _));

    [Theory]
    [InlineData(EditorSubjectKinds.FictionalEntity, "name", false)]
    [InlineData(EditorSubjectKinds.FictionalEntity, "description", false)]
    [InlineData(EditorSubjectKinds.FictionalEntity, "aliases", false)]
    [InlineData(EditorSubjectKinds.FictionalEntity, "belongs_to", true)]
    [InlineData(EditorSubjectKinds.FictionalEntity, "visibility", true)]
    [InlineData(EditorSubjectKinds.Collection, "title", true)]
    [InlineData(EditorSubjectKinds.Series, "title", false)]
    [InlineData(EditorSubjectKinds.Universe, "title", false)]
    [InlineData(EditorSubjectKinds.Person, "name", false)]
    [InlineData(EditorSubjectKinds.Person, "sort_name", true)]
    [InlineData(EditorSubjectKinds.Person, "custom_tags", true)]
    public void ASubjectsField_IsItsOwn_OnlyWhereNoProviderFillsIt(string kind, string key, bool own) =>
        Assert.Equal(own, EditorFieldLocks.IsOwnSubjectField(kind, key));

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
