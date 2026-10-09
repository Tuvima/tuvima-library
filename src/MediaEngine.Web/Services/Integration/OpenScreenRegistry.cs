using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>One open Dashboard screen (a Blazor circuit) and the sign-in it is using.</summary>
/// <param name="ScreenId">Identifies this screen only; never shown or sent anywhere.</param>
/// <param name="SessionTokenHash">A hash of the session token, so the registry never holds the token itself.</param>
/// <param name="Ingress">Where the screen was opened from (<c>this_computer</c>, <c>home_network</c>, <c>remote</c>).</param>
public sealed record OpenScreen(
    Guid ScreenId,
    Guid? AccountId,
    Guid? ProfileId,
    Guid? SessionId,
    string SessionTokenHash,
    string Ingress);

/// <summary>
/// Knows which Dashboard screens are open so a big access change (a person removed, a device signed out,
/// "who can connect" lowered) can send them to sign-in at once. The one-minute check on each screen remains the
/// backstop for everything else. Screens register when a circuit opens and unregister when it closes.
/// </summary>
public sealed class OpenScreenRegistry(ILogger<OpenScreenRegistry>? logger = null)
{
    private readonly ConcurrentDictionary<Guid, (OpenScreen Screen, Action Close)> _screens = new();

    /// <summary>How many screens are open right now.</summary>
    public int Count => _screens.Count;

    /// <summary>Adds an open screen. Disposing the result removes it, so a closed circuit never lingers.</summary>
    public IDisposable Register(OpenScreen screen, Action close)
    {
        _screens[screen.ScreenId] = (screen, close);
        return new Registration(this, screen.ScreenId);
    }

    /// <summary>Sends every matching screen to sign-in. Returns how many were closed.</summary>
    public int CloseWhere(Func<OpenScreen, bool> matches)
    {
        var closed = 0;
        foreach (var (screen, close) in _screens.Values.ToArray())
        {
            if (!matches(screen))
            {
                continue;
            }

            try
            {
                close();
                closed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One screen failing to close must not leave the rest signed in; the minute check still catches it.
                logger?.LogWarning(exception, "Could not close an open screen after an access change.");
            }
        }

        return closed;
    }

    /// <summary>Closes every screen opened from farther away than <paramref name="whoCanConnect"/> now allows.</summary>
    public int CloseWhereIngressNotAllowed(string? whoCanConnect) =>
        CloseWhere(screen => !IsIngressAllowed(whoCanConnect, screen.Ingress));

    /// <summary>The Dashboard's one door rule, applied to a screen's remembered starting place.</summary>
    public static bool IsIngressAllowed(string? whoCanConnect, string? ingress) =>
        // HTTPS is not part of this check: the distance rule is what changes when the setting is lowered.
        ExposurePolicy.Evaluate(whoCanConnect, IngressClassifierExtensions.FromWireValue(ingress), isHttps: true)
            != ExposureDecision.NotAvailableHere;

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class Registration(OpenScreenRegistry owner, Guid id) : IDisposable
    {
        public void Dispose() => owner._screens.TryRemove(id, out _);
    }
}
