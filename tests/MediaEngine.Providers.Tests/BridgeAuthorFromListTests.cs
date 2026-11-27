using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Authors are stored as a list, so the Wikidata step must read the first listed author for a book or audiobook;
/// without one it searches by title alone and a common title can match another author's book.
/// </summary>
public sealed class BridgeAuthorFromListTests
{
    private sealed class ListSpy(Guid entityId, params string[] authors) : ICanonicalValueArrayRepository
    {
        public Task SetValuesAsync(Guid entityId, string key, IReadOnlyList<CanonicalArrayEntry> entries, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CanonicalArrayEntry>> GetValuesAsync(Guid entityId, string key, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CanonicalArrayEntry>>([]);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>>> GetAllByEntityAsync(Guid id, CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, IReadOnlyList<CanonicalArrayEntry>> result = id == entityId
                ? new Dictionary<string, IReadOnlyList<CanonicalArrayEntry>>
                {
                    ["author"] = authors.Select((name, index) => new CanonicalArrayEntry { Ordinal = index, Value = name }).ToList(),
                }
                : new Dictionary<string, IReadOnlyList<CanonicalArrayEntry>>();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<CanonicalArrayEntry>> FindValuesByKeyAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CanonicalArrayEntry>>([]);

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ABook_IsLookedUpByItsFirstListedAuthor()
    {
        var id = Guid.NewGuid();

        var author = await WikidataBridgeWorker.ReadAuthorFromArraysAsync(
            new ListSpy(id, "Martha Wells", "Someone Else"), NullLogger.Instance, MediaType.Books, id, null, CancellationToken.None);

        Assert.Equal("Martha Wells", author);
    }

    [Fact]
    public async Task AFilmOrSong_IsNotGivenAnAuthor_AndNoListMeansNoAuthor()
    {
        var id = Guid.NewGuid();

        Assert.Null(await WikidataBridgeWorker.ReadAuthorFromArraysAsync(
            new ListSpy(id, "Anyone"), NullLogger.Instance, MediaType.Movies, id, null, CancellationToken.None));
        Assert.Null(await WikidataBridgeWorker.ReadAuthorFromArraysAsync(
            null, NullLogger.Instance, MediaType.Books, id, null, CancellationToken.None));
    }
}
