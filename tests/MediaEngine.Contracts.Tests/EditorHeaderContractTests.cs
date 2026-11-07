using System.Text.Json;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the editor's v5 header, current match, document details and folder tree (universal editor v5, WP3).</summary>
public sealed class EditorHeaderContractTests
{
    [Fact]
    public void Header_UsesSnakeCaseNames_AndLeavesOutWhatIsNotThere()
    {
        var show = Guid.NewGuid();
        var season = Guid.NewGuid();
        var header = new EditorHeaderDto
        {
            Kind = "tv_episode",
            Root = new EditorHeaderRootDto { Id = show, Title = "The Expanse", MediaType = "TV" },
            CurrentId = season,
            Label = "The Expanse",
            ShowHome = true,
            HomeId = show,
            ShowItemDropdown = true,
            Colors = new EditorCoverColorsDto { PrimaryHex = "#112233", SecondaryHex = "#445566", AccentHex = "#778899" },
        };

        var json = JsonSerializer.Serialize(header);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("tv_episode", root.GetProperty("kind").GetString());
        Assert.Equal(show, root.GetProperty("root").GetProperty("id").GetGuid());
        Assert.Equal("The Expanse", root.GetProperty("root").GetProperty("title").GetString());
        Assert.True(root.GetProperty("show_home").GetBoolean());
        Assert.Equal(show, root.GetProperty("home_id").GetGuid());
        Assert.True(root.GetProperty("show_item_dropdown").GetBoolean());
        Assert.Equal("#112233", root.GetProperty("colors").GetProperty("primary_hex").GetString());
        Assert.Equal("#778899", root.GetProperty("colors").GetProperty("accent_hex").GetString());
        Assert.False(root.TryGetProperty("second_level", out _));
        Assert.False(root.TryGetProperty("folder_button", out _));
        Assert.False(root.TryGetProperty("file_facts", out _));
    }

    [Fact]
    public void HeaderLevel_RoundTrips_WithItsCodesStatesAndSupportingFiles()
    {
        var episode = Guid.NewGuid();
        var file = Guid.NewGuid();
        var level = new EditorHeaderLevelDto
        {
            LevelTitle = "Season 2",
            Total = 120,
            Offset = 50,
            PageSize = 50,
            HasMore = true,
            Items =
            [
                new EditorNavItemDto
                {
                    Id = episode, Kind = "episode", Code = "S02E03", Number = 3, Title = "Caliban's War",
                    State = EditorNavItemStates.NotMatched, DurationSeconds = 2580, IsCurrent = true,
                },
            ],
            SupportingFiles = new EditorHeaderSupportingFilesDto
            {
                ScopeKey = "season:2",
                Count = 9,
                TotalInRoot = 11,
                ShowAll = true,
                Files = [new EditorHeaderSupportingFileDto { Id = file, Kind = "document", Title = "Guide", TypeBadge = "PDF", Role = "guide", RoleLabel = "Guide", PageCount = 12, ItemCount = 1 }],
            },
        };

        var back = JsonSerializer.Deserialize<EditorHeaderLevelDto>(JsonSerializer.Serialize(level))!;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(level));
        var item = document.RootElement.GetProperty("items")[0];

        Assert.Equal("S02E03", item.GetProperty("code").GetString());
        Assert.Equal("not_matched", item.GetProperty("state").GetString());
        Assert.Equal(2580, item.GetProperty("duration_seconds").GetDouble());
        Assert.True(document.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(120, document.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("season:2", document.RootElement.GetProperty("supporting_files").GetProperty("scope_key").GetString());
        Assert.Equal(11, back.SupportingFiles!.TotalInRoot);
        Assert.Equal("PDF", Assert.Single(back.SupportingFiles.Files).TypeBadge);
        Assert.True(Assert.Single(back.Items).IsCurrent);
    }

    [Fact]
    public void ASupportingFileRow_CarriesWhereItIsShownAlongside_AndWhetherItIsTheOneBeingEdited()
    {
        var alongside = new EditorHeaderSupportingFileDto
        {
            Id = Guid.NewGuid(),
            Title = "Component exercises",
            TypeBadge = "PDF",
            Role = "worksheet",
            RoleLabel = "Worksheet",
            Group = "Homework",
            ShownAlongside = new EditorHeaderAlongsideDto { Key = "file:02 Components/02 Passing parameters.mkv", Label = "with lesson 02" },
            IsCurrent = true,
        };
        var plain = new EditorHeaderSupportingFileDto { Id = Guid.NewGuid(), Title = "Notes" };

        using var withTarget = JsonDocument.Parse(JsonSerializer.Serialize(alongside));
        using var without = JsonDocument.Parse(JsonSerializer.Serialize(plain));
        var back = JsonSerializer.Deserialize<EditorHeaderSupportingFileDto>(JsonSerializer.Serialize(alongside))!;

        var target = withTarget.RootElement.GetProperty("shown_alongside");
        Assert.Equal("file:02 Components/02 Passing parameters.mkv", target.GetProperty("key").GetString());
        Assert.Equal("with lesson 02", target.GetProperty("label").GetString());
        Assert.True(withTarget.RootElement.GetProperty("is_current").GetBoolean());
        Assert.False(without.RootElement.TryGetProperty("shown_alongside", out _));
        Assert.False(without.RootElement.GetProperty("is_current").GetBoolean());
        Assert.Equal("with lesson 02", back.ShownAlongside!.Label);
        Assert.True(back.IsCurrent);
    }

    [Fact]
    public void TheKindConfig_NamesItsKind_AndCarriesTabsFactsAndSearchFields()
    {
        var config = new EditorHeaderConfigDto
        {
            Kind = "person",
            RowsPerPage = 25,
            Tabs = ["details", "artwork", "match", "history"],
            FactsLine = ["roles"],
            QuickFacts = ["item_count"],
            MatchFields = ["title"],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(config));

        Assert.Equal("person", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal(25, document.RootElement.GetProperty("rows_per_page").GetInt32());
        Assert.Equal("match", document.RootElement.GetProperty("tabs")[2].GetString());
        Assert.Equal("title", document.RootElement.GetProperty("match_fields")[0].GetString());
    }

    [Fact]
    public void NavItemStates_AreTheWordsTheDropdownShows()
    {
        Assert.Equal("matched", EditorNavItemStates.Matched);
        Assert.Equal("not_matched", EditorNavItemStates.NotMatched);
        Assert.Equal("watched", EditorNavItemStates.Watched);
        Assert.Equal("unwatched", EditorNavItemStates.Unwatched);
        Assert.Equal("none", EditorNavItemStates.None);
    }

    [Fact]
    public void EditorContext_CarriesTheHeader_OnlyWhenThereIsOne()
    {
        var without = JsonSerializer.Serialize(new MediaEditorContextDto());
        var with = JsonSerializer.Serialize(new MediaEditorContextDto { Header = new EditorHeaderDto { Kind = "movie" } });

        Assert.DoesNotContain("\"header\"", without, StringComparison.Ordinal);
        Assert.Contains("\"header\":{\"kind\":\"movie\"", with, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentMatch_UsesSnakeCaseNames_AndKeepsTheWhyRows()
    {
        var match = new EditorCurrentMatchDto
        {
            EntityId = Guid.NewGuid(),
            State = EditorCurrentMatchStates.Matched,
            Record = new EditorMatchedRecordDto
            {
                ProviderId = "tvdb",
                ProviderName = "TheTVDB",
                ProviderItemId = "121361",
                Kind = "Show",
                Title = "Game of Thrones",
                Year = "2011",
                SourceUrl = "https://thetvdb.com/series/121361",
                Source = EditorCurrentMatchSources.Provider,
            },
            WikidataQid = "Q23572",
            Why =
            [
                new EditorMatchEvidenceRowDto { Field = "title", YourFile = "Game of Thrones", MatchedRecord = "Game of Thrones", Score = 0.97, State = EditorMatchEvidenceStates.Agrees },
                new EditorMatchEvidenceRowDto { Field = "year", MatchedRecord = "2011", State = EditorMatchEvidenceStates.NotChecked },
            ],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(match));
        var root = document.RootElement;
        var record = root.GetProperty("record");

        Assert.Equal("matched", root.GetProperty("state").GetString());
        Assert.Equal("tvdb", record.GetProperty("provider_id").GetString());
        Assert.Equal("121361", record.GetProperty("provider_item_id").GetString());
        Assert.Equal("provider", record.GetProperty("source").GetString());
        Assert.Equal("Q23572", root.GetProperty("wikidata_qid").GetString());
        Assert.Equal(0.97, root.GetProperty("why")[0].GetProperty("score").GetDouble());
        Assert.Equal("agrees", root.GetProperty("why")[0].GetProperty("state").GetString());
        Assert.False(root.GetProperty("why")[1].TryGetProperty("your_file", out _));
        Assert.False(root.GetProperty("why")[1].TryGetProperty("score", out _));
    }

    [Fact]
    public void CurrentMatch_ForAnUnmatchedItem_HasNoRecord()
    {
        var json = JsonSerializer.Serialize(new EditorCurrentMatchDto { EntityId = Guid.NewGuid() });

        Assert.Contains("\"state\":\"not_matched\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"record\"", json, StringComparison.Ordinal);
        Assert.Contains("\"why\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestDetails_UsesSnakeCaseNames()
    {
        var id = Guid.NewGuid();
        var suggestions = new EditorSuggestDetailsDto
        {
            SupportingFileId = id,
            Source = EditorSuggestDetailsSources.Pdf,
            Title = "Worksheet",
            Description = "Maths",
            Author = "A. Teacher",
            PageCount = 4,
            FirstLines = ["Line one", "Line two"],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(suggestions));
        var root = document.RootElement;

        Assert.Equal(id, root.GetProperty("supporting_file_id").GetGuid());
        Assert.Equal("pdf", root.GetProperty("source").GetString());
        Assert.Equal(4, root.GetProperty("page_count").GetInt32());
        Assert.Equal(["Line one", "Line two"], root.GetProperty("first_lines").EnumerateArray().Select(line => line.GetString()));
        Assert.Equal(["pdf", "epub", "text", "none"],
            new[] { EditorSuggestDetailsSources.Pdf, EditorSuggestDetailsSources.Epub, EditorSuggestDetailsSources.Text, EditorSuggestDetailsSources.None });
    }

    [Fact]
    public void FolderTree_UsesSnakeCaseNames_AndLeavesOutTheStateOfAFolder()
    {
        var item = Guid.NewGuid();
        var tree = new EditorFolderTreeDto
        {
            RootName = "The Expanse",
            Folder = "Season 02",
            ParentFolder = string.Empty,
            ReadOnly = true,
            Total = 2,
            Offset = 0,
            HasMore = false,
            Entries =
            [
                new EditorFolderEntryDto { Name = "Extras", Path = "Season 02/Extras", IsFolder = true },
                new EditorFolderEntryDto { Name = "e01.mkv", Path = "Season 02/e01.mkv", State = EditorFolderEntryStates.Main, SizeBytes = 1000, ItemId = item },
                new EditorFolderEntryDto { Name = "e01.nfo", Path = "Season 02/e01.nfo", State = EditorFolderEntryStates.Hidden, SizeBytes = 10 },
            ],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tree));
        var root = document.RootElement;
        var entries = root.GetProperty("entries");

        Assert.Equal("The Expanse", root.GetProperty("root_name").GetString());
        Assert.True(root.GetProperty("read_only").GetBoolean());
        Assert.Equal("Season 02", root.GetProperty("folder").GetString());
        Assert.True(entries[0].GetProperty("is_folder").GetBoolean());
        Assert.False(entries[0].TryGetProperty("state", out _));
        Assert.Equal("main", entries[1].GetProperty("state").GetString());
        Assert.Equal(item, entries[1].GetProperty("item_id").GetGuid());
        Assert.Equal("hidden", entries[2].GetProperty("state").GetString());
        Assert.Equal(["main", "supporting", "hidden", "unsorted"],
            new[] { EditorFolderEntryStates.Main, EditorFolderEntryStates.Supporting, EditorFolderEntryStates.Hidden, EditorFolderEntryStates.Unsorted });
    }
}
