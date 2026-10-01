using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorDetailsPresenterTests
{
    [Fact]
    public void Build_UsesDraftThenProfileThenLibraryThenOwnCanonicalThenAllowedParent()
    {
        var parent = Scope("album", canonical: [Field("record_label", "Parent label")]);
        var active = Scope("track", parentId: "album", allowed: ["genre", "record_label"],
            canonical: [Field("title", "Canonical title"), Field("genre", "Own genre")],
            overrides: new Dictionary<string, string> { ["title"] = "Library title" });
        var scopes = new Dictionary<string, MediaEditorScopeDto>(StringComparer.OrdinalIgnoreCase)
        {
            [parent.ScopeId] = parent,
            [active.ScopeId] = active,
        };

        var presentation = MediaEditorDetailsPresenter.Build(active, scopes, "Music",
            draft: new Dictionary<string, string> { ["title"] = "Draft title" },
            profileOverrides: new Dictionary<string, string> { ["title"] = "Profile title", ["genre"] = "Profile genre" });

        Assert.Equal("Draft title", Get(presentation, "title").Value);
        Assert.Equal("Override", Get(presentation, "title").Provenance);
        Assert.Equal("Profile genre", Get(presentation, "genre").Value);
        Assert.Equal("/listen/music?genre=Profile%20genre", Get(presentation, "genre").LinkedValues![0].Href);
        var noDrafts = MediaEditorDetailsPresenter.Build(active, scopes, "Music");
        Assert.Equal("Library title", Get(noDrafts, "title").RawValue);
        Assert.Equal("Own genre", Get(noDrafts, "genre").RawValue);
        Assert.Equal("Parent label", Get(noDrafts, "record_label").RawValue);
        Assert.Equal("Inherited", Get(noDrafts, "record_label").Provenance);
        Assert.Equal("album", Get(noDrafts, "record_label").SourceLabel);
    }

    [Fact]
    public void Build_InheritsTypeApprovedFieldAndPrefersOwnAirDate()
    {
        var parent = Scope("series", canonical: [Field("genre", "Parent genre"), Field("air_date", "2001-09-04")]);
        var active = Scope("episode", parentId: "series", canonical: [Field("air_date", "2026-01-02")]);
        var presentation = MediaEditorDetailsPresenter.Build(active,
            new Dictionary<string, MediaEditorScopeDto> { ["series"] = parent, ["episode"] = active }, "TV");

        Assert.Contains(presentation.Fields, field => field.Key == "genre" && field.Provenance == "Inherited");
        var airDate = Get(presentation, "air_date");
        Assert.Equal("2026-01-02", airDate.RawValue);
        Assert.Equal("January 2, 2026", airDate.DisplayValue);
    }

    [Fact]
    public void Build_FormatsDatesForReadingAndPreservesRawInlineValue()
    {
        var scope = Scope("movie", canonical: [Field("release_date", "2024-01-07")]);
        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        var release = Get(presentation, "release_date");
        Assert.Equal("January 7, 2024", release.DisplayValue);
        Assert.Equal("2024-01-07", release.RawValue);
    }

    [Fact]
    public void Build_FormatsIsoTimestampUsingItsCalendarDateWithoutTimezoneConversion()
    {
        var scope = Scope("episode", canonical: [Field("air_date", "2024-01-07T00:00:00Z")]);
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["episode"] = scope }, "TV");

        var airDate = Get(presentation, "air_date");
        Assert.Equal("January 7, 2024", airDate.DisplayValue);
        Assert.Equal("2024-01-07T00:00:00Z", airDate.RawValue);
    }

    [Fact]
    public void Build_EpisodeCompactFactsOmitSeasonAndEpisodeNumbers()
    {
        var scope = Scope("episode", canonical:
        [
            Field("season_number", "1"), Field("episode_number", "1"), Field("air_date", "2024-01-07"),
            Field("runtime", "24"), Field("rating", "8.4"), Field("content_rating", "TV-14"),
        ]);
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["episode"] = scope }, "TV");

        Assert.Contains(presentation.Facts, field => field.Key == "air_date");
        Assert.Contains(presentation.Facts, field => field.Key == "runtime");
        Assert.Contains(presentation.Facts, field => field.Key == "rating");
        Assert.Contains(presentation.Facts, field => field.Key == "content_rating");
        Assert.DoesNotContain(presentation.Facts, field => field.Key is "season_number" or "episode_number");
    }

    [Fact]
    public void Build_FormatsMusicTrackPositionFactsWithTheirMeaning()
    {
        var scope = Scope("track", canonical: [Field("track_number", "13"), Field("disc_number", "1")]);
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["track"] = scope }, "Music");

        var track = Get(presentation, "track_number");
        Assert.Equal("Track 13", track.DisplayValue);
        Assert.Equal("13", track.RawValue);
        Assert.False(track.CanOverride);
        var disc = Get(presentation, "disc_number");
        Assert.Equal("Disc 1", disc.DisplayValue);
        Assert.Equal("1", disc.RawValue);
    }

    [Fact]
    public void Build_FormatsCompactBookSeriesPositionAsAPosition()
    {
        var scope = Scope("book", canonical: [Field("series_position", "2.5")]);
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["book"] = scope }, "Books");

        var position = Get(presentation, "series_position");
        Assert.Equal("Position 2.5", position.DisplayValue);
        Assert.Equal("2.5", position.RawValue);
    }

    [Fact]
    public void Build_InheritsOnlyTypeApprovedReadOnlyFieldsWithoutRequiringOverridePermission()
    {
        var parent = Scope("series", canonical: [Field("network", "HBO")]);
        var active = Scope("episode", parentId: "series");
        var presentation = MediaEditorDetailsPresenter.Build(active,
            new Dictionary<string, MediaEditorScopeDto> { ["series"] = parent, ["episode"] = active }, "TV");

        var network = Get(presentation, "network");
        Assert.Equal("Inherited", network.Provenance);
        Assert.Equal("HBO", network.RawValue);
        Assert.False(network.CanOverride);
        Assert.Equal("series", network.SourceScopeId);
    }

    [Fact]
    public void Build_PreservesCanonicalArrayOrderAndCommasInsideContributorNames()
    {
        var scope = Scope("book", canonicalArrays:
        [
            ArrayField("author", (2, "A. Writer, Jr."), (1, "B. Writer")),
            ArrayField("genre", (1, "Science Fiction"), (2, "Adventure")),
        ], allowed: ["genre"]);

        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["book"] = scope }, "Books");

        var author = Get(presentation, "author");
        Assert.Equal("B. Writer; A. Writer, Jr.", author.DisplayValue);
        Assert.True(author.IsChips);
        Assert.False(author.CanOverride);
        var genre = Get(presentation, "genre");
        Assert.Equal("Science Fiction, Adventure", genre.DisplayValue);
        Assert.True(genre.IsChips);
        Assert.True(genre.CanOverride);
        Assert.Equal("/read/books?genre=Science%20Fiction", genre.LinkedValues![0].Href);
    }

    [Fact]
    public void Build_LinksOnlyVerifiedPeopleAndLeavesUnresolvedOrOverriddenNamesAsText()
    {
        var personId = Guid.NewGuid();
        var scope = Scope("movie", canonicalArrays:
        [
            new MediaEditorScopedFieldArrayDto
            {
                Key = "cast_member",
                OwnerEntityId = Guid.NewGuid(),
                OwnerEntityKind = "Work",
                Entries =
                [
                    new() { Ordinal = 1, Value = "Doe, Jane", LocalPersonId = personId },
                    new() { Ordinal = 2, Value = "Unresolved, Person" },
                ],
            },
        ]);

        var canonical = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");
        var cast = Get(canonical, "cast_member");
        Assert.Equal("Doe, Jane; Unresolved, Person", cast.DisplayValue);
        Assert.Equal("/details/person/" + personId.ToString("D"), cast.LinkedValues![0].Href);
        Assert.Null(cast.LinkedValues[1].Href);

        var overridden = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies",
            profileOverrides: new Dictionary<string, string> { ["cast_member"] = "Replacement, Actor" });
        Assert.Equal("Replacement, Actor", Get(overridden, "cast_member").RawValue);
        Assert.Null(Get(overridden, "cast_member").LinkedValues);
    }

    [Fact]
    public void Build_UsesAlbumAsEditableHeadingAndAvoidsDuplicatingArtistCredits()
    {
        var scope = Scope("album", allowed: ["title"], canonical: [Field("album", "Madness"), Field("track_count", "13")],
            canonicalArrays: [ArrayField("artist", (1, "All That Remains"))]);
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["album"] = scope }, "Music");

        Assert.NotNull(presentation.Heading);
        Assert.Equal("Madness", presentation.Heading!.DisplayValue);
        Assert.Equal("title", presentation.Heading.OverrideKey);
        Assert.True(presentation.Heading.CanOverride);
        Assert.Contains(presentation.Fields, field => field.Label == "Album artist" && field.RawValue == "All That Remains");
        Assert.DoesNotContain(presentation.Fields, field => field.Label == "Credits");
        Assert.Contains(presentation.Facts, field => field.Key == "track_count" && field.DisplayValue == "13 tracks" && field.RawValue == "13");
    }

    [Fact]
    public void Build_InheritsCanonicalArraysFromActualParentScopeAsReadOnly()
    {
        var series = Scope("series", canonicalArrays: [ArrayField("genre", (1, "Drama"), (2, "Mystery"))]);
        var season = Scope("season", parentId: "series");
        var episode = Scope("episode", parentId: "season", allowed: ["genre"]);
        var scopes = new Dictionary<string, MediaEditorScopeDto>
        {
            ["series"] = series,
            ["season"] = season,
            ["episode"] = episode,
        };

        var presentation = MediaEditorDetailsPresenter.Build(episode, scopes, "TV");

        var genre = Get(presentation, "genre");
        Assert.Equal("Drama, Mystery", genre.RawValue);
        Assert.Equal("Inherited", genre.Provenance);
        Assert.Equal("series", genre.SourceScopeId);
        Assert.False(genre.CanOverride);
        Assert.False(genre.CanRevert);
        Assert.Equal("/watch/tv?genre=Drama", genre.LinkedValues![0].Href);
    }

    [Fact]
    public void Build_LinksEachGenreInLocalOverridesIndividually()
    {
        var scope = Scope("movie", overrides: new Dictionary<string, string> { ["genre"] = "Adventure, Drama" });
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        var genre = Get(presentation, "genre");
        Assert.Equal(new[] { "Adventure", "Drama" }, genre.LinkedValues!.Select(value => value.Label));
        Assert.Equal("/watch/movies?genre=Adventure", genre.LinkedValues![0].Href);
        Assert.Equal("/watch/movies?genre=Drama", genre.LinkedValues![1].Href);
    }

    [Fact]
    public void Build_LinksInheritedParentGenreOverrideButKeepsItReadOnly()
    {
        var parent = Scope("series", overrides: new Dictionary<string, string> { ["genre"] = "Science Fiction, Mystery" });
        var active = Scope("episode", parentId: "series", allowed: ["genre"]);
        var presentation = MediaEditorDetailsPresenter.Build(active,
            new Dictionary<string, MediaEditorScopeDto> { ["series"] = parent, ["episode"] = active }, "TV");

        var genre = Get(presentation, "genre");
        Assert.Equal("Inherited", genre.Provenance);
        Assert.Equal("series", genre.SourceScopeId);
        Assert.False(genre.CanOverride);
        Assert.False(genre.CanRevert);
        Assert.Equal(new[] { "Science Fiction", "Mystery" }, genre.LinkedValues!.Select(value => value.Label));
        Assert.Equal("/watch/tv?genre=Science%20Fiction", genre.LinkedValues![0].Href);
        Assert.Equal("/watch/tv?genre=Mystery", genre.LinkedValues![1].Href);
    }

    [Fact]
    public void Build_EpisodePrefersItsOwnAirDateAndKeepsShowFirstAirDateOutOfEpisodeFacts()
    {
        var parent = Scope("series", canonical: [Field("first_air_date", "2001-09-04")]);
        var active = Scope("episode", parentId: "series", allowed: ["first_air_date"], canonical: [Field("air_date", "2026-02-03")]);
        var presentation = MediaEditorDetailsPresenter.Build(active,
            new Dictionary<string, MediaEditorScopeDto> { ["series"] = parent, ["episode"] = active }, "TV");

        Assert.Equal("2026-02-03", Get(presentation, "air_date").RawValue);
        Assert.DoesNotContain(presentation.Facts, field => field.Key == "first_air_date");
    }

    [Fact]
    public void Build_EpisodeUsesEpisodeTitleBeforeGenericTitleButTitleOverrideWins()
    {
        var scope = Scope("episode", allowed: ["title"], canonical:
        [
            Field("title", "Generic file title"),
            Field("episode_title", "Episode specific title"),
        ]);
        var scopes = new Dictionary<string, MediaEditorScopeDto> { ["episode"] = scope };

        var canonical = MediaEditorDetailsPresenter.Build(scope, scopes, "TV");
        var overlaid = MediaEditorDetailsPresenter.Build(scope, scopes, "TV",
            profileOverrides: new Dictionary<string, string> { ["title"] = "User display title" });

        Assert.Equal("episode_title", Get(canonical, "episode_title").Key);
        Assert.Equal("title", Get(canonical, "episode_title").OverrideKey);
        Assert.Equal("Episode specific title", Get(canonical, "episode_title").RawValue);
        Assert.Equal("User display title", Get(overlaid, "episode_title").RawValue);
    }

    [Theory]
    [InlineData("Movies", "movie", "portrait")]
    [InlineData("TV", "episode", "landscape")]
    [InlineData("Music", "track", "square")]
    [InlineData("Books", "book", "portrait")]
    [InlineData("Audiobooks", "audiobook", "portrait")]
    [InlineData("Comics", "issue", "portrait")]
    public void Build_UsesTypeAndScopePresentation(string mediaType, string scopeId, string expectedShape)
    {
        var scope = Scope(scopeId, canonical: [Field("title", "A title"), Field("rating", "8.4"), Field("bridge_id", "not displayed")]);
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { [scopeId] = scope }, mediaType);

        Assert.Equal(expectedShape, presentation.ImageShape);
        Assert.Equal("A title", Get(presentation, "title").Value);
        Assert.Contains(presentation.Facts, field => field.Key == "rating");
        Assert.Contains(presentation.Fields, field => field.Key == "rating");
        Assert.DoesNotContain(All(presentation), field => field.Key == "bridge_id");
    }

    [Fact]
    public void Build_ShowsOnlyPopulatedCatalogFieldsAndDoesNotOfferPersonOverrides()
    {
        var scope = Scope("movie", allowed: ["cast_member", "genre", "custom_tags"], canonical:
        [
            Field("title", "The Film"), Field("genre", "Drama, Mystery"), Field("cast_member", "Actor Name"), Field("custom_tags", "quiet"), Field("unused", "hidden")
        ]);

        var presentation = MediaEditorDetailsPresenter.Build(scope, new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        Assert.Contains(presentation.Fields, field => field.Key == "genre" && field.IsChips && field.CanOverride);
        Assert.Contains(presentation.Fields, field => field.Key == "cast_member" && field.IsChips && !field.CanOverride);
        Assert.Contains(presentation.Fields, field => field.Key == "custom_tags" && field.IsChips && field.CanOverride);
        Assert.DoesNotContain(All(presentation), field => field.Key is "unused" or "bridge_id");
        Assert.DoesNotContain(presentation.AdditionalFields, field => string.IsNullOrWhiteSpace(field.Value));
    }

    [Fact]
    public void Build_LibraryTagsIgnoreLegacyProfileOverrides()
    {
        var scope = Scope("movie", allowed: ["custom_tags"],
            canonical: [Field("title", "The Film")],
            overrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom_tags"] = "library tag; another",
            });

        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope },
            "Movies",
            profileOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom_tags"] = "legacy profile tag",
            });

        var tags = Get(presentation, "custom_tags");
        Assert.Equal("library tag; another", tags.RawValue);
        Assert.Equal("Library override", tags.SourceLabel);
    }

    [Fact]
    public void Build_PreservesOrderedCanonicalOrganizationArraysWithoutSplittingScalarNames()
    {
        var movie = Scope("movie", canonicalArrays:
        [
            ArrayField("production_company", (1, "Acme, Inc."), (2, "North Studio"), (3, "Final Films")),
            ArrayField("country_of_origin", (1, "Canada"), (2, "Japan")),
        ]);
        var moviePresentation = MediaEditorDetailsPresenter.Build(movie,
            new Dictionary<string, MediaEditorScopeDto> { ["movie"] = movie }, "Movies");
        var companies = Get(moviePresentation, "production_company");
        var countries = Get(moviePresentation, "country_of_origin");

        Assert.True(companies.IsChips);
        Assert.Equal(new[] { "Acme, Inc.", "North Studio", "Final Films" }, companies.LinkedValues!.Select(value => value.Label));
        Assert.All(companies.LinkedValues!, value => Assert.Null(value.Href));
        Assert.True(countries.IsChips);
        Assert.Equal(new[] { "Canada", "Japan" }, countries.LinkedValues!.Select(value => value.Label));

        var book = Scope("book", canonical: [Field("title", "A book"), Field("publisher", "Morrow, LLC")]);
        var bookPresentation = MediaEditorDetailsPresenter.Build(book,
            new Dictionary<string, MediaEditorScopeDto> { ["book"] = book }, "Books");
        var publisher = Get(bookPresentation, "publisher");
        Assert.False(publisher.IsChips);
        Assert.Equal("Morrow, LLC", publisher.RawValue);
    }

    [Fact]
    public void Build_EmptyLibraryTagSentinelSuppressesOlderCanonicalTags()
    {
        var scope = Scope("movie", allowed: ["custom_tags"],
            canonical: [Field("title", "The Film"), Field("custom_tags", "old provider tag")],
            overrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom_tags"] = string.Empty,
            });

        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        Assert.DoesNotContain(All(presentation), field => field.Key == "custom_tags");
        Assert.True(presentation.CanAddTags);
    }

    [Fact]
    public void Build_OffersEmptyLibraryTagsWhenScopeAllowsEditingWithoutProfileState()
    {
        var scope = Scope("movie", allowed: ["custom_tags"], canonical: [Field("title", "The Film")]);
        var presentation = MediaEditorDetailsPresenter.Build(scope,
            new Dictionary<string, MediaEditorScopeDto> { ["movie"] = scope }, "Movies");

        Assert.True(presentation.CanAddTags);
    }

    [Fact]
    public void Build_UsesOnlyPreferredEditionWhenWorkHasSiblingEditions()
    {
        var editionOne = Guid.NewGuid();
        var editionTwo = Guid.NewGuid();
        var scope = Scope("book", canonical:
        [
            Field("title", "Work title"),
            Field("publisher", "First Publisher", editionOne, "Edition"),
            Field("publisher", "Second Publisher", editionTwo, "Edition"),
        ]);
        var scopes = new Dictionary<string, MediaEditorScopeDto> { ["book"] = scope };

        var selected = MediaEditorDetailsPresenter.Build(scope, scopes, "Books", preferredFieldOwnerEntityId: editionTwo);

        Assert.Equal("Second Publisher", Get(selected, "publisher").RawValue);
        Assert.Equal("Edition", Get(selected, "publisher").SourceLabel);
    }

    private static MediaEditorDetailsFieldPresentation Get(MediaEditorDetailsPresentation presentation, string key)
    {
        bool Matches(MediaEditorDetailsFieldPresentation field) =>
            string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase);

        // Compact facts intentionally repeat selected values from the metadata
        // table. Prefer the editable/detail row, then its dedicated heading or
        // synopsis slot, and use the compact strip only when it is the sole row.
        return (presentation.Heading is { } heading && Matches(heading) ? heading : null)
               ?? presentation.Fields.FirstOrDefault(Matches)
               ?? presentation.AdditionalFields.FirstOrDefault(Matches)
               ?? (presentation.Synopsis is { } synopsis && Matches(synopsis) ? synopsis : null)
               ?? presentation.Facts.FirstOrDefault(Matches)
               ?? throw new InvalidOperationException($"No presented field matched '{key}'.");
    }

    private static IEnumerable<MediaEditorDetailsFieldPresentation> All(MediaEditorDetailsPresentation presentation)
    {
        var fields = presentation.Facts.Concat(presentation.Fields).Concat(presentation.AdditionalFields);
        if (presentation.Heading is { } heading) fields = fields.Append(heading);
        return presentation.Synopsis is { } synopsis ? fields.Append(synopsis) : fields;
    }

    private static MediaEditorScopeDto Scope(
        string id,
        string? parentId = null,
        IEnumerable<string>? allowed = null,
        IEnumerable<MediaEditorScopedFieldValueDto>? canonical = null,
        Dictionary<string, string>? overrides = null,
        IEnumerable<MediaEditorScopedFieldArrayDto>? canonicalArrays = null) => new()
        {
            ScopeId = id,
            Label = id,
            CanEditFields = true,
            FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
            {
                ParentFieldScopeId = parentId,
                AllowedOverrideKeys = allowed?.ToList() ?? [],
                CanonicalFields = canonical?.ToList() ?? [],
                CanonicalArrays = canonicalArrays?.ToList() ?? [],
                DisplayOverrides = overrides ?? new(StringComparer.OrdinalIgnoreCase),
            },
        };

    private static MediaEditorScopedFieldValueDto Field(string key, string value, Guid? ownerId = null, string ownerKind = "Work") => new()
    {
        Key = key,
        Value = value,
        OwnerEntityId = ownerId ?? Guid.NewGuid(),
        OwnerEntityKind = ownerKind,
    };

    private static MediaEditorScopedFieldArrayDto ArrayField(string key, params (int Ordinal, string Value)[] entries) => new()
    {
        Key = key,
        OwnerEntityId = Guid.NewGuid(),
        OwnerEntityKind = "Work",
        Entries = entries.Select(entry => new MediaEditorScopedArrayEntryDto { Ordinal = entry.Ordinal, Value = entry.Value }).ToList(),
    };
}
