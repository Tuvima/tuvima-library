using MediaEngine.Domain.Models;
using MediaEngine.Storage.Benchmarks.Seeding;

namespace MediaEngine.Storage.Benchmarks;

/// <summary>
/// The fixed parameter sets every variant of a query receives. Derived only from the dataset size (no randomness
/// beyond fixed strides), so Dapper and EF are always asked identical questions and reruns repeat exactly.
/// </summary>
internal sealed class Workload
{
    public const int IdBatchSize = 500;
    public const int FeedPageSize = 50;
    public const int ItemPageSize = 50;
    public const int HomeLimit = 1000;

    public Workload(DatasetInfo info)
    {
        Works = info.Works;

        // 500 ids mixing media-asset ids and work ids, spread over the whole dataset with a fixed stride.
        Q1Ids = BuildIds(stride: 197, shift: 13);
        Q2Ids = BuildIds(stride: 211, shift: 7);

        FeedDeepOffset = info.Works / 2;
        ItemsUnfiltered = new LibraryItemQuery(Offset: 0, Limit: ItemPageSize);
        ItemsMediaType = new LibraryItemQuery(Offset: 100, Limit: ItemPageSize, MediaType: "Movies");
    }

    public int Works { get; }

    /// <summary>Q1: canonical_values batch read.</summary>
    public IReadOnlyList<Guid> Q1Ids { get; }

    /// <summary>Q2: metadata_claims batch read.</summary>
    public IReadOnlyList<Guid> Q2Ids { get; }

    /// <summary>Q3: offset of the deep page (about 50k at full scale).</summary>
    public int FeedDeepOffset { get; }

    public LibraryItemQuery ItemsUnfiltered { get; }

    public LibraryItemQuery ItemsMediaType { get; }

    private List<Guid> BuildIds(int stride, int shift)
    {
        var ids = new List<Guid>(IdBatchSize);
        for (var i = 0; i < IdBatchSize; i++)
        {
            var seq = 1 + ((long)i * stride + shift) % Works;
            ids.Add(i % 2 == 0 ? SeedIds.Id(SeedIds.Asset, seq) : SeedIds.Id(SeedIds.Work, seq));
        }

        return ids;
    }
}
