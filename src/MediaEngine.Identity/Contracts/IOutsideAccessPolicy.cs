namespace MediaEngine.Identity.Contracts;

/// <summary>
/// Whether away-from-home access is switched on right now (remote-access spec D6). While it is off, nobody signs in from
/// outside the home network, and requests that are clearly from outside (see <c>ClientIngress.IsClearlyOutside</c>) are not
/// served, whatever way in is still running. Only a known way in counts as on; anything unknown is off. The answer may be
/// kept for a few seconds, and <see cref="Invalidate"/> clears it when the settings are saved, so switching it off applies at once.
/// </summary>
public interface IOutsideAccessPolicy
{
    bool IsOutsideAccessOn();

    /// <summary>Forgets any kept answer so the next question reads the settings again. A policy that keeps nothing has nothing to do.</summary>
    void Invalidate()
    {
    }
}

/// <summary>Ends every session that was signed in from outside the home network (used when outside access is switched off).</summary>
public interface IOutsideSessionEnder
{
    /// <returns>How many active sessions were ended.</returns>
    Task<int> EndOutsideSessionsAsync(string reason, CancellationToken ct = default);
}
