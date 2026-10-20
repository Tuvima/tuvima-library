using MediaEngine.Contracts.Details;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Remembers, for a minute, the answer to "does this profile get a 'Part of the … Universe' line for this Universe?".
/// That answer needs the profile's whole authorised projection, and an item page asks it on every load, so the same
/// profile opening several pages of one Universe asks the library once per minute instead of once per page.
/// </summary>
/// <remarks>
/// Visibility is never widened by the cache: the key includes the profile, so one profile's answer is never given to
/// another. The cost of remembering is that a content-limit or library change takes up to <see cref="Lifetime"/> to show
/// in this one quiet line (the Universe page itself is never cached). The cache is bounded to <see cref="MaxEntries"/>
/// answers.
/// </remarks>
public sealed class UniverseLinkCache : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public const int MaxEntries = 2048;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    /// <summary>The remembered answer, or the answer from <paramref name="load"/>, which is then remembered (a "no link" answer too).</summary>
    public async Task<UniverseLinkViewModel?> GetOrLoadAsync(
        Guid? profileId,
        string universeQid,
        Func<Task<UniverseLinkViewModel?>> load)
    {
        var key = $"{profileId:N}|{universeQid}";
        if (_cache.TryGetValue(key, out Remembered? remembered) && remembered is not null)
        {
            return remembered.Link;
        }

        var link = await load().ConfigureAwait(false);
        _cache.Set(key, new Remembered(link), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Lifetime });
        return link;
    }

    public void Dispose() => _cache.Dispose();

    private sealed record Remembered(UniverseLinkViewModel? Link);
}
