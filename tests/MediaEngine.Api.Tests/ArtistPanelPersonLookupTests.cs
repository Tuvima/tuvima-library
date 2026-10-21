using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Storage;
using Xunit;

namespace MediaEngine.Api.Tests;

public sealed class ArtistPanelPersonLookupTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_artist_panel_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public ArtistPanelPersonLookupTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task FindArtistPerson_PanelArtistExists_ReturnsThePanelArtistNotThePickedCredit()
    {
        var panelArtistId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        InsertPeople((panelArtistId, "Daft Punk"), (guestId, "Pharrell Williams"));

        var person = await CollectionEndpoints.FindArtistPersonAsync(
            new PersonRepository(_db), "Daft Punk", "Pharrell Williams", CancellationToken.None);

        Assert.NotNull(person);
        Assert.Equal(panelArtistId, person.Id);
    }

    [Fact]
    public async Task FindArtistPerson_NobodyHasThePanelArtistsName_FallsBackToThePickedCredit()
    {
        var pickedId = Guid.NewGuid();
        InsertPeople((pickedId, "Artist One"));

        var person = await CollectionEndpoints.FindArtistPersonAsync(
            new PersonRepository(_db), "artist one feat. guest", "Artist One", CancellationToken.None);

        Assert.NotNull(person);
        Assert.Equal(pickedId, person.Id);
    }

    [Fact]
    public async Task FindArtistPerson_NeitherNameIsKnown_ReturnsNobody()
    {
        var person = await CollectionEndpoints.FindArtistPersonAsync(
            new PersonRepository(_db), "Unknown Artist", null, CancellationToken.None);

        Assert.Null(person);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // The temp file is left for the OS to clean up if SQLite still holds it.
        }
    }

    private void InsertPeople(params (Guid Id, string Name)[] people)
    {
        using var connection = _db.CreateConnection();
        foreach (var (id, name) in people)
        {
            connection.Execute(
                "INSERT INTO persons (id, name, created_at) VALUES (@id, @name, CURRENT_TIMESTAMP);",
                new { id, name });
        }
    }
}
