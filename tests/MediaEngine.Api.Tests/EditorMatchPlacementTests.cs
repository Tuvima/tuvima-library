using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Where applying a match would put the item and its file (universal editor v5, D8): the place the chosen record names, found among the
/// places the Library already has, and whether the file follows. Over the Library move's own services and real temp folders, so what the
/// Match tab shows is what Save changes does. A Managed folder moves; a Read-only or As-is folder never does.
/// </summary>
public sealed class EditorMatchPlacementTests : IDisposable
{
    private const string OkComputerManifest = """
        {"tracks":[
          {"title":"Airbag","track_number":1,"disc_number":1,"duration_seconds":284},
          {"title":"Paranoid Android","track_number":2,"disc_number":1,"duration_seconds":387}]}
        """;

    private readonly LibraryMoveTestWorld _world = new(withFiles: true);

    private MoveFileWorld Files => _world.Files!;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task ASongMatchedToAnotherAlbum_ReParents_AndAManagedFileFollows_WithBothPaths()
    {
        var okComputer = OkComputer();
        var song = BestOfSong(Files.Managed, out var oldPath);

        var placement = await PlanAsync(song, Candidate("OK Computer"));

        Assert.NotNull(placement);
        Assert.Equal(okComputer, placement.DestinationId);
        Assert.StartsWith("The Best Of", placement.CurrentParentLabel, StringComparison.Ordinal);
        Assert.StartsWith("OK Computer", placement.ParentLabel, StringComparison.Ordinal);
        Assert.True(placement.Managed);
        Assert.Equal(Path.GetFullPath(oldPath), placement.FromPath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Files.Managed, "Music", "Radiohead", "OK Computer (1997)", "02 - Paranoid Android.flac")),
            placement.ToPath);
        Assert.Null(placement.BlockedReason);
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("as-is")]
    public async Task AFileInAFolderThatDoesNotMove_StillReParents_ButHasNoFileMove(string folder)
    {
        var okComputer = OkComputer();
        var song = BestOfSong(folder == "read-only" ? Files.ReadOnly : Files.AsIs, out _);

        var placement = await PlanAsync(song, Candidate("OK Computer"));

        Assert.NotNull(placement);
        Assert.Equal(okComputer, placement.DestinationId);
        Assert.False(placement.Managed);
        Assert.Null(placement.FromPath);
        Assert.Null(placement.ToPath);
        Assert.False(string.IsNullOrWhiteSpace(placement.BlockedReason));
    }

    [Fact]
    public async Task AnAlbumTheLibraryDoesNotHave_IsNeverCreated_AndTheItemStaysWhereItIs()
    {
        OkComputer();
        var song = BestOfSong(Files.Managed, out _);

        var placement = await PlanAsync(song, Candidate("Kid A"));

        Assert.NotNull(placement);
        Assert.Null(placement.DestinationId);
        Assert.Null(placement.ParentLabel);
        Assert.Contains("Kid A", placement.BlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultThatNamesNoPlace_LeavesTheItemWhereItIs()
    {
        OkComputer();
        var song = BestOfSong(Files.Managed, out _);

        var placement = await PlanAsync(song, Candidate(album: null));

        Assert.NotNull(placement);
        Assert.Null(placement.DestinationId);
    }

    [Fact]
    public async Task AnItemTheLibraryDoesNotHave_HasNoPlacement()
    {
        Assert.Null(await PlanAsync(Guid.NewGuid(), Candidate("OK Computer")));
    }

    [Theory]
    [InlineData("TV", "Long Runner", "2", "Season 2", "Long Runner")]
    [InlineData("TV", "Long Runner", "0", "Specials", "Long Runner")]
    [InlineData("TV", "Long Runner", null, "Long Runner", null)]
    public void ATvResult_NamesItsSeasonUnderItsShow(string mediaType, string show, string? season, string label, string? parent)
    {
        var candidate = new ItemCanonicalRetailCandidateDto();
        candidate.RequiredFields[MetadataFieldConstants.ShowName] = show;
        if (season is not null)
        {
            candidate.RequiredFields[MetadataFieldConstants.SeasonNumber] = season;
        }

        var proposed = EditorMatchPlacementService.ProposedParentOf(candidate, mediaType);

        Assert.Equal(label, proposed!.Label);
        Assert.Equal(parent, proposed.ParentLabel);
    }

    // ----- helpers -----------------------------------------------------------------------------------------------------------

    private Task<EditorMatchPlacementDto?> PlanAsync(Guid item, ItemCanonicalRetailCandidateDto candidate) =>
        new EditorMatchPlacementService(
            _world.Service,
            _world.Repository,
            new ItemCanonicalRepository(_world.Database, NullLogger<ItemCanonicalRepository>.Instance),
            Files.Organizer).PlanAsync(item, candidate, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything);

    private static ItemCanonicalRetailCandidateDto Candidate(string? album)
    {
        var candidate = new ItemCanonicalRetailCandidateDto { Title = "Paranoid Android", IsApplicable = true };
        if (album is not null)
        {
            candidate.RequiredFields[MetadataFieldConstants.Album] = album;
            candidate.RequiredFields[MetadataFieldConstants.Artist] = "Radiohead";
        }

        return candidate;
    }

    private Guid OkComputer()
    {
        var album = _world.AddParent("Music", "OK Computer", manifestJson: OkComputerManifest);
        _world.Put(album, "year", "1997");
        _world.AddChild(album, "Music", "Airbag", 1);
        return album;
    }

    private Guid BestOfSong(string folder, out string path)
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        _world.Put(bestOf, "year", "2000");
        path = MoveFileWorld.WriteFile(folder, "Music/Radiohead/The Best Of (2000)/04 - Paranoid Android.flac");
        var (work, _, asset) = _world.AddChild(bestOf, "Music", "Paranoid Android", 4, filePath: path);
        _world.Put(asset!.Value, "title", "Paranoid Android");
        _world.Put(asset.Value, "media_type", "Music");
        _world.Put(asset.Value, "artist", "Radiohead");
        _world.Put(asset.Value, "album", "The Best Of");
        _world.Put(asset.Value, "track_number", "4");
        return work;
    }
}
