using System.Reflection;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The media editor's Match &amp; Identity section shows the identity status and artwork source the detail page
/// used to show on its Registry tab. Both come from the same producers the detail composer uses.
/// </summary>
public sealed class EditorContextIdentityFactsTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_editor_identity_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public EditorContextIdentityFactsTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task EditorContext_ReportsTheIdentityStatusAndArtworkSourceTheDetailComposerReports()
    {
        var workId = await InsertBookAsync();
        var items = new LibraryItemRepository(_database);
        var detail = await items.GetDetailAsync(workId);
        Assert.NotNull(detail);

        var context = await ResolveEditorScopeContextAsync(workId);

        Assert.NotNull(context);
        Assert.Equal(
            DetailPresentationPolicy.ResolveIdentityStatus(detail.WikidataQid, detail.Status, detail.Confidence).ToString(),
            ReadFact(context, "IdentityStatus"));
        Assert.Equal(
            DetailPresentationPolicy.ResolveArtworkSource(detail.ArtworkSource).ToString(),
            ReadFact(context, "ArtworkSource"));
    }

    [Fact]
    public async Task EditorContext_LeavesBothFactsEmptyWhenTheItemHasNoLibraryDetail()
    {
        var context = await ResolveEditorScopeContextAsync(Guid.NewGuid());

        Assert.Null(context);
    }

    [Theory]
    [InlineData("Q42", "Identified", 0.95, CanonicalIdentityStatus.WikidataLinked)]
    [InlineData(null, "NeedsReview", 0.95, CanonicalIdentityStatus.NeedsReview)]
    [InlineData(null, "Identified", 0.5, CanonicalIdentityStatus.NeedsReview)]
    [InlineData(null, "Identified", 0.95, CanonicalIdentityStatus.ProviderMatched)]
    public void IdentityStatus_FollowsTheWikidataLinkThenReviewStateThenConfidence(
        string? qid, string status, double confidence, CanonicalIdentityStatus expected)
    {
        Assert.Equal(expected, DetailPresentationPolicy.ResolveIdentityStatus(qid, status, confidence));
    }

    [Theory]
    [InlineData(null, ArtworkSource.Generated)]
    [InlineData("", ArtworkSource.Generated)]
    [InlineData("user", ArtworkSource.User)]
    [InlineData("Manual upload", ArtworkSource.User)]
    [InlineData("provider", ArtworkSource.Provider)]
    [InlineData("embedded", ArtworkSource.Provider)]
    public void ArtworkSource_NamesWhoSuppliedTheCover(string? source, ArtworkSource expected)
    {
        Assert.Equal(expected, DetailPresentationPolicy.ResolveArtworkSource(source));
    }

    [Fact]
    public void EditorContextContract_CarriesBothFactsAsOptionalSnakeCaseStrings()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new MediaEditorContextDto
        {
            IdentityStatus = "WikidataLinked",
            ArtworkSource = "User",
        });

        Assert.Contains("\"identity_status\":\"WikidataLinked\"", json, StringComparison.Ordinal);
        Assert.Contains("\"artwork_source\":\"User\"", json, StringComparison.Ordinal);
        Assert.Null(new MediaEditorContextDto().IdentityStatus);
        Assert.Null(new MediaEditorContextDto().ArtworkSource);
    }

    private async Task<object?> ResolveEditorScopeContextAsync(Guid entityId)
    {
        var method = typeof(MetadataEndpoints).GetMethod(
            "ResolveEditorScopeContextAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(null,
        [
            entityId,
            new CanonicalValueRepository(_database),
            new LibraryItemRepository(_database),
            new MetadataEditorRepository(_database),
            CancellationToken.None,
        ])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static string? ReadFact(object context, string name) =>
        (string?)context.GetType().GetProperty(name)!.GetValue(context);

    private async Task<Guid> InsertBookAsync()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@workId, 'Book', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets
                (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.epub",
                libraryId = Guid.NewGuid().ToString("D"),
            });
        return workId;
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // SQLite can release a pooled test handle just after disposal; the OS temp sweep removes the file.
        }
    }
}
