using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

// The light "which works may this profile see" read must name exactly the works the full read returns, for every profile shape.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly IReadOnlySet<AccountFeatureId> EveryFeature =
        new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch, AccountFeatureId.Listen };

    [Fact]
    public async Task VisibleWorkIds_AreTheWorkIdsOfTheFullReadForAnUnrestrictedOwner()
    {
        var shape = await SeedVisibleWorkShapeAsync();
        var owner = Guid.NewGuid();
        await CreateHumanAsync(owner, EveryFeature, new HashSet<Guid>(), administrator: true);

        var visible = await AssertVisibleIdsMatchFullReadAsync(HumanContext(owner, ProfileOf(owner)));

        Assert.Equal(shape.All.Order(), visible.Order());
    }

    [Fact]
    public async Task VisibleWorkIds_AreTheWorkIdsOfTheFullReadForAProfileWithALibraryHidden()
    {
        var shape = await SeedVisibleWorkShapeAsync();
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { shape.LibraryB });

        var visible = await AssertVisibleIdsMatchFullReadAsync(HumanContext(account, ProfileOf(account)));

        Assert.Equal(shape.InLibraryA.Order(), visible.Order());
        Assert.Contains(shape.InBothLibraries, visible);
        Assert.DoesNotContain(shape.OnlyInLibraryB, visible);
    }

    [Fact]
    public async Task VisibleWorkIds_AreTheWorkIdsOfTheFullReadForAProfileLimitedByMediaType()
    {
        var shape = await SeedVisibleWorkShapeAsync();
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid>());

        var visible = await AssertVisibleIdsMatchFullReadAsync(HumanContext(account, ProfileOf(account)));

        Assert.Equal(shape.Movies.Order(), visible.Order());
    }

    [Theory]
    [InlineData("PG", false)]
    [InlineData("PG", true)]
    [InlineData("PG-13", false)]
    [InlineData("R", true)]
    [InlineData(null, false)]
    public async Task VisibleWorkIds_AreTheWorkIdsOfTheFullReadForAProfileLimitedByContentRating(string? limit, bool allowUnrated)
    {
        var shape = await SeedVisibleWorkShapeAsync();
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());
        SetContentLimit(limit, allowUnrated);

        var visible = await AssertVisibleIdsMatchFullReadAsync(HumanContext(account, ProfileOf(account)));

        // The fixture holds rated works above the strictest limit, so a limit must hide something and keep something.
        Assert.NotEmpty(visible);
        if (limit == "PG")
        {
            Assert.True(visible.Count < shape.All.Count);
        }
    }

    [Fact]
    public async Task VisibleWorkIds_AreEmptyWhenTheRequestHasNoValidAuthority()
    {
        await SeedVisibleWorkShapeAsync();

        var visible = await AssertVisibleIdsMatchFullReadAsync(new DefaultHttpContext());

        Assert.Empty(visible);
    }

    [Fact]
    public async Task HomeCollectionsAreFilteredByTheSameVisibleWorkIdsTheFullReadYields()
    {
        var shape = await SeedVisibleWorkShapeAsync();
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { shape.LibraryB });
        var raw = new ReaderBackedProjection(new DisplayWorkProjectionReader(_database), new DisplayVisibleWorkReader(_database));
        var service = CreateService(HumanContext(account, ProfileOf(account)), raw);

        await service.LoadHomeCollectionsAsync(MediaEngine.Domain.Aggregates.Profile.SeedProfileId, CancellationToken.None);

        Assert.Equal(shape.InLibraryA.Order(), raw.AllowedWorkIdsPassedToHomeCollections!.Order());
        Assert.Equal(0, raw.FullReadCount);
    }

    private async Task<IReadOnlySet<Guid>> AssertVisibleIdsMatchFullReadAsync(DefaultHttpContext context)
    {
        var service = CreateService(
            context,
            new ReaderBackedProjection(new DisplayWorkProjectionReader(_database), new DisplayVisibleWorkReader(_database)));

        var expected = (await service.LoadWorksAsync(CancellationToken.None)).Select(row => row.WorkId).ToHashSet();
        var actual = await service.LoadVisibleWorkIdsAsync(CancellationToken.None);

        Assert.Equal(expected.Order(), actual.Order());
        return actual;
    }

    // Works in two libraries across books, movies and music, with ratings from G to R, one unrated, one work in both
    // libraries and one rated only on its file.
    private async Task<VisibleWorkShape> SeedVisibleWorkShapeAsync()
    {
        var libraryA = Guid.NewGuid();
        var libraryB = Guid.NewGuid();
        var inA = new List<Guid>();
        var all = new List<Guid>();
        var movies = new List<Guid>();

        async Task<Guid> AddAsync(Guid library, string mediaType, string? rating, bool ratingOnFile = false)
        {
            var (workId, assetId) = await InsertOwnedWorkWithIdAsync(library, $"{mediaType} {all.Count}", mediaType);
            if (rating is not null)
            {
                using var connection = _database.CreateConnection();
                await connection.ExecuteAsync(
                    "INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@owner,'content_rating',@rating,CURRENT_TIMESTAMP);",
                    new { owner = ratingOnFile ? assetId : workId, rating });
            }

            all.Add(workId);
            if (library == libraryA)
            {
                inA.Add(workId);
            }

            if (mediaType == "Movies")
            {
                movies.Add(workId);
            }

            return workId;
        }

        await AddAsync(libraryA, "Books", "G");
        await AddAsync(libraryA, "Books", null);
        await AddAsync(libraryA, "Movies", "PG");
        await AddAsync(libraryA, "Movies", "R");
        await AddAsync(libraryA, "Movies", "PG-13", ratingOnFile: true);
        await AddAsync(libraryA, "Music", null);
        await AddAsync(libraryB, "Books", "R");
        await AddAsync(libraryB, "Movies", "G");
        var onlyInB = await AddAsync(libraryB, "Movies", null);
        await AddAsync(libraryB, "Music", "PG");

        // One movie owned in both libraries: visible to a profile that has either.
        var both = await AddAsync(libraryA, "Movies", "PG");
        var secondEdition = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO editions(id, work_id) VALUES (@secondEdition, @both);
                INSERT INTO media_assets(id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@secondAsset, @secondEdition, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
                """,
                new
                {
                    secondEdition,
                    both,
                    secondAsset,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"C:/library/{secondAsset:N}.mkv",
                    libraryId = libraryB.ToString("D"),
                });
        }

        return new VisibleWorkShape(libraryA, libraryB, all, inA, movies, both, onlyInB);
    }

    private sealed record VisibleWorkShape(
        Guid LibraryA,
        Guid LibraryB,
        IReadOnlyList<Guid> All,
        IReadOnlyList<Guid> InLibraryA,
        IReadOnlyList<Guid> Movies,
        Guid InBothLibraries,
        Guid OnlyInLibraryB);

    // Reads the real store with both the full and the light reader, and records what the home collections read was given.
    private sealed class ReaderBackedProjection(
        DisplayWorkProjectionReader works,
        DisplayVisibleWorkReader visibleWorks) : IRawDisplayProjectionReadService
    {
        public int FullReadCount { get; private set; }
        public IReadOnlySet<Guid>? AllowedWorkIdsPassedToHomeCollections { get; private set; }

        public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct)
        {
            FullReadCount++;
            return await works.LoadAsync(ct);
        }

        public Task<IReadOnlyList<DisplayVisibleWorkRow>> LoadVisibleWorkRowsAsync(CancellationToken ct) => visibleWorks.LoadAsync(ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);

        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(
            Guid? profileId,
            IReadOnlySet<Guid> allowedWorkIds,
            CancellationToken ct)
        {
            AllowedWorkIdsPassedToHomeCollections = allowedWorkIds;
            return Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
        }
    }
}
