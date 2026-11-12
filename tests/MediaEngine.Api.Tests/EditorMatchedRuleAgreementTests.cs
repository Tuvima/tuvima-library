using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What "matched" means in the editor is one rule (<see cref="EditorMatchRule"/>). The Match tab's own read, the family navigation read and the
/// owned-child list (the supporting-files and ticked-rows reads) each take their answer from it, so a key one of them counts is counted by all.
/// </summary>
public sealed class EditorMatchedRuleAgreementTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_matched_rule_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public EditorMatchedRuleAgreementTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
    }

    public void Dispose()
    {
        _database.Dispose();
        try { File.Delete(_path); } catch (IOException) { /* a temp file another process still holds is left for the temp folder clean-up */ }
    }

    [Fact]
    public async Task EveryKeyTheRuleCountsAsAnIdentity_IsMatchedInTheNavigationRead_AndInTheOwnedChildList()
    {
        foreach (var key in EditorMatchRule.IdentityKeys)
        {
            var movieId = AddMovieWith(key, "value-1");

            Assert.Equal("matched", await NavigationState(movieId));
            Assert.Equal("matched", await OwnedChildState(movieId));
        }
    }

    [Theory]
    [InlineData("imdb_id")]
    [InlineData("title")]
    [InlineData("isbn")]
    public async Task AKeyTheRuleDoesNotCount_LeavesTheItemUnmatched_InBothReads(string key)
    {
        Assert.DoesNotContain(key, EditorMatchRule.IdentityKeys);
        var movieId = AddMovieWith(key, "value-1");

        Assert.Equal("unmatched", await NavigationState(movieId));
        Assert.Equal("unmatched", await OwnedChildState(movieId));
    }

    [Fact]
    public async Task AnEmptyValueUnderAnIdentityKey_IsNotAMatch_InEitherRead()
    {
        var movieId = AddMovieWith(EditorMatchRule.IdentityKeys[0], string.Empty);

        Assert.Equal("unmatched", await NavigationState(movieId));
        Assert.Equal("unmatched", await OwnedChildState(movieId));
    }

    [Fact]
    public void EveryKeyThatMakesAProviderMatch_IsAlsoAnIdentityKeyExceptAnIsbn()
    {
        var notIdentity = EditorMatchRule.ProviderMatchKeys.Except(EditorMatchRule.IdentityKeys).ToList();

        Assert.Equal(EditorMatchRule.IsbnKeys.OrderBy(key => key), notIdentity.OrderBy(key => key));
    }

    private async Task<string> NavigationState(Guid movieId)
    {
        var service = new MediaEditorNavigationReadService(_database, null!, new HierarchyAlignmentService(_database, null!));
        var navigator = await service.GetNavigatorAsync(movieId, CancellationToken.None);
        Assert.NotNull(navigator);
        return Assert.Single(navigator.Nodes).MatchState;
    }

    private async Task<string> OwnedChildState(Guid movieId)
    {
        var owned = await new MediaEditorOwnedChildReadService(_database)
            .SearchAsync(movieId, null, 1, 50, null, null, null, null, null, CancellationToken.None);
        Assert.NotNull(owned);
        return Assert.Single(owned.Items).MatchState;
    }

    private Guid AddMovieWith(string key, string value)
    {
        var movieId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@movieId, 'Movies', 'standalone', 'Owned');
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @movieId, 'MKV');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @hash, @file);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@movieId, @key, @value, datetime('now'));
            """, new
        {
            movieId,
            editionId = Guid.NewGuid(),
            assetId = Guid.NewGuid(),
            hash = $"hash-{movieId:N}",
            file = $"/movies/{movieId:N}.mkv",
            key,
            value,
        });
        return movieId;
    }
}
