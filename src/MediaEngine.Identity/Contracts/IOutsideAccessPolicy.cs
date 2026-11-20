namespace MediaEngine.Identity.Contracts;

/// <summary>
/// Whether away-from-home access is switched on right now (remote-access spec D6). While it is off, nobody signs in from
/// outside the home network, and requests from outside are not served, whatever way in is still running. It is read when a
/// decision is made, never cached, so switching it off applies at once.
/// </summary>
public interface IOutsideAccessPolicy
{
    bool IsOutsideAccessOn();
}

/// <summary>Ends every session that was signed in from outside the home network (used when outside access is switched off).</summary>
public interface IOutsideSessionEnder
{
    /// <returns>How many active sessions were ended.</returns>
    Task<int> EndOutsideSessionsAsync(string reason, CancellationToken ct = default);
}
