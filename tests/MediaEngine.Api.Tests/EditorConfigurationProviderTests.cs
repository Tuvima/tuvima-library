using MediaEngine.Api.Services.EditSession;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

/// <summary>config/editor.json (universal editor v5, D34): the editor's numbers and per-kind rules, with the shipped rules as the fallback.</summary>
public sealed class EditorConfigurationProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima_editor_config_{Guid.NewGuid():N}");

    public EditorConfigurationProviderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void WithNoConfigFile_TheShippedRulesApply()
    {
        var config = new EditorConfigurationProvider(() => _directory).Current;

        Assert.Equal(8, config.InlineSupportingFiles);
        Assert.Equal(12, config.CardsPerGroup);
        Assert.Equal(25, config.RowsPerPage);
        Assert.Equal(50, config.ItemListPageSize);
        Assert.Equal(100, config.FolderTreePageSize);
        Assert.Equal(6, config.SuggestDetails.FirstLines);
    }

    [Fact]
    public void TheShippedFileInTheRepository_IsTheSameAsTheOneBuiltIntoTheEngine()
    {
        var shipped = EditorConfigurationProvider.TryRead(File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "editor.json")), out var problem);

        Assert.Null(problem);
        Assert.NotNull(shipped);
        Assert.Equal(EditorConfigurationProvider.BuiltIn.InlineSupportingFiles, shipped.InlineSupportingFiles);
        Assert.Equal(EditorConfigurationProvider.BuiltIn.Kinds.Keys.Order(), shipped.Kinds.Keys.Order());
    }

    [Theory]
    [InlineData(EditorKinds.TvShow, new[] { "details", "artwork", "match", "history" })]
    [InlineData(EditorKinds.TvSeason, new[] { "details", "artwork", "match", "history" })]
    [InlineData(EditorKinds.TvEpisode, new[] { "details", "artwork", "match", "history" })]
    [InlineData(EditorKinds.Course, new[] { "details", "artwork", "history" })]
    [InlineData(EditorKinds.Video, new[] { "details", "artwork", "history" })]
    [InlineData(EditorKinds.Series, new[] { "details", "history" })]
    [InlineData(EditorKinds.SupportingFile, new[] { "details", "preview", "history" })]
    [InlineData(EditorKinds.Universe, new[] { "details", "pictures", "match", "history" })]
    [InlineData(EditorKinds.ViewItem, new[] { "details", "history" })]
    [InlineData(EditorKinds.ViewGallery, new[] { "details", "artwork", "history" })]
    public void EachKind_GetsTheTabsOfTheSpec_AndNeverAContentsOrFilesTab(string kind, string[] tabs)
    {
        var rules = EditorConfigurationProvider.BuiltIn.KindFor(kind);

        Assert.Equal(tabs, rules.Tabs);
        Assert.DoesNotContain("contents", rules.Tabs);
        Assert.DoesNotContain("files", rules.Tabs);
    }

    [Theory]
    [InlineData(EditorKinds.Movie, new[] { "title", "year", "provider_id" })]
    [InlineData(EditorKinds.TvShow, new[] { "show", "year" })]
    [InlineData(EditorKinds.Album, new[] { "artist", "album", "song" })]
    [InlineData(EditorKinds.Book, new[] { "title", "author", "isbn" })]
    [InlineData(EditorKinds.Comic, new[] { "series", "issue", "year" })]
    [InlineData(EditorKinds.Audiobook, new[] { "title", "author_or_narrator" })]
    public void EachKind_GetsItsMatchSearchFields(string kind, string[] fields) =>
        Assert.Equal(fields, EditorConfigurationProvider.BuiltIn.KindFor(kind).MatchFields);

    [Fact]
    public void AKindTheFileDoesNotKnow_FallsBackToTheGenericItem() =>
        Assert.Same(EditorConfigurationProvider.BuiltIn.Kinds[EditorKinds.Item], EditorConfigurationProvider.BuiltIn.KindFor("something_new"));

    [Fact]
    public void AConfigFile_ChangesTheNumbers_AndIsReadAgainWhenItChanges()
    {
        var provider = new EditorConfigurationProvider(() => _directory);
        Assert.Equal(8, provider.Current.InlineSupportingFiles);

        WriteConfig(inlineSupportingFiles: 3);
        Assert.Equal(3, provider.Current.InlineSupportingFiles);

        WriteConfig(inlineSupportingFiles: 5);
        File.SetLastWriteTimeUtc(Path.Combine(_directory, "editor.json"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(5, provider.Current.InlineSupportingFiles);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":\"1\",\"inline_supporting_files\":0,\"cards_per_group\":1,\"rows_per_page\":1,\"item_list_page_size\":1,\"kinds\":{\"item\":{\"tabs\":[\"details\"]}}}")]
    [InlineData("{\"version\":\"1\",\"inline_supporting_files\":4,\"cards_per_group\":1,\"rows_per_page\":1,\"item_list_page_size\":1,\"kinds\":{\"item\":{\"tabs\":[\"history\"]}}}")]
    [InlineData("{\"version\":\"1\",\"cards_per_group\":-3}")]
    [InlineData("{\"version\":\"1\",\"suggest_details\":{\"first_lines\":0}}")]
    public void ABrokenFile_IsNotUsed_AndTheShippedRulesApplyInstead(string content)
    {
        File.WriteAllText(Path.Combine(_directory, "editor.json"), content);

        var config = new EditorConfigurationProvider(() => _directory).Current;

        Assert.Equal(8, config.InlineSupportingFiles);
        Assert.Same(EditorConfigurationProvider.BuiltIn, config);
    }

    [Fact]
    public void AFileThatOmitsAKey_GetsTheShippedValue_AndKeepsTheKeysItStates()
    {
        File.WriteAllText(Path.Combine(_directory, "editor.json"), "{ \"version\": \"1\", \"inline_supporting_files\": 3 }");
        var shipped = EditorConfigurationProvider.BuiltIn;

        var config = new EditorConfigurationProvider(() => _directory).Current;

        Assert.Equal(3, config.InlineSupportingFiles);
        Assert.Equal(shipped.CardsPerGroup, config.CardsPerGroup);
        Assert.Equal(shipped.RowsPerPage, config.RowsPerPage);
        Assert.Equal(shipped.ItemListPageSize, config.ItemListPageSize);
        Assert.Equal(shipped.FolderTreePageSize, config.FolderTreePageSize);
        Assert.Equal(shipped.SuggestDetails.FirstLines, config.SuggestDetails.FirstLines);
        Assert.Equal(shipped.SuggestDetails.DescriptionMaxCharacters, config.SuggestDetails.DescriptionMaxCharacters);
        Assert.Equal(shipped.Kinds.Keys.Order(), config.Kinds.Keys.Order());
    }

    [Fact]
    public void AFileThatChangesOneKindOnly_KeepsEveryOtherKindFromTheShippedRules()
    {
        File.WriteAllText(
            Path.Combine(_directory, "editor.json"),
            "{ \"version\": \"1\", \"kinds\": { \"movie\": { \"tabs\": [\"details\", \"history\"], \"facts_line\": [], \"quick_facts\": [], \"match_fields\": [\"title\"] } } }");

        var config = new EditorConfigurationProvider(() => _directory).Current;

        Assert.Equal(["details", "history"], config.KindFor(EditorKinds.Movie).Tabs);
        Assert.Equal(EditorConfigurationProvider.BuiltIn.KindFor(EditorKinds.Course).Tabs, config.KindFor(EditorKinds.Course).Tabs);
        Assert.Equal(EditorConfigurationProvider.BuiltIn.InlineSupportingFiles, config.InlineSupportingFiles);
    }

    [Fact]
    public void TheCountsOfTheShippedFile_AreNotWrittenInCode()
    {
        // A count the code carries would survive a file that leaves the key out; the blueprint holds none, so an unset count reads as missing.
        var blank = new EditorConfiguration();

        Assert.Equal(0, blank.InlineSupportingFiles);
        Assert.Equal(0, blank.CardsPerGroup);
        Assert.Equal(0, blank.RowsPerPage);
        Assert.Equal(0, blank.ItemListPageSize);
        Assert.Equal(0, blank.FolderTreePageSize);
        Assert.Equal(0, blank.SuggestDetails.FirstLines);
        Assert.Equal(0, blank.SuggestDetails.DescriptionMaxCharacters);
        Assert.Null(EditorConfigurationProvider.TryRead("{ \"version\": \"1\" }", out var problem));
        Assert.NotNull(problem);
    }

    private void WriteConfig(int inlineSupportingFiles) => File.WriteAllText(
        Path.Combine(_directory, "editor.json"),
        $$"""
        {
          "version": "1",
          "inline_supporting_files": {{inlineSupportingFiles}},
          "cards_per_group": 12,
          "rows_per_page": 25,
          "item_list_page_size": 50,
          "folder_tree_page_size": 100,
          "kinds": { "item": { "tabs": ["details", "history"], "facts_line": [], "quick_facts": [], "match_fields": [] } }
        }
        """);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The OS temp sweep removes what is left.
        }
    }
}
