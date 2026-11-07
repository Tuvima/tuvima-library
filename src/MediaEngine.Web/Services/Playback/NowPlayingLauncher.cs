namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// How the rest of the app asks the persistent bar to open or close Now playing (the navbar icon). The bar owns the decision: it alone knows whether it is
/// drawn for a phone or a desktop, so a request runs the same handler as its own Now playing tool and cover click (unified player v2 D24).
/// </summary>
public sealed class NowPlayingLauncher
{
    /// <summary>Raised when the navbar icon is pressed while audio is playing.</summary>
    public event Action? Requested;

    public void Request() => Requested?.Invoke();
}
