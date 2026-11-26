namespace MediaEngine.Providers.Services;

/// <summary>
/// Runs an expensive lookup once and shares the answer with everyone who asks for the same key at the same
/// time or later. The lookup is not cancelled when one caller gives up; each caller only stops waiting.
/// An answer that <c>isUsable</c> rejects, or a lookup that throws, is forgotten so the next caller tries again.
/// </summary>
internal sealed class SharedWalkMemo<T>
{
    private readonly LookupMemo<Task<T>> _walks;

    public SharedWalkMemo(TimeSpan lifetime, TimeProvider? clock = null) => _walks = new(lifetime, clock);

    public async Task<T> GetAsync(string key, Func<Task<T>> walk, Func<T, bool> isUsable, CancellationToken ct)
    {
        if (_walks.TryGet(key, out var shared))
        {
            try
            {
                var known = await shared.WaitAsync(ct).ConfigureAwait(false);
                if (isUsable(known))
                {
                    return known;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The shared lookup failed for its first caller; fall through and run it again for this one.
            }

            _walks.Remove(key);
        }

        var started = walk();
        _walks.Set(key, started);
        try
        {
            var answer = await started.WaitAsync(ct).ConfigureAwait(false);
            if (!isUsable(answer))
            {
                _walks.Remove(key);
            }

            return answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _walks.Remove(key);
            throw;
        }
    }
}
