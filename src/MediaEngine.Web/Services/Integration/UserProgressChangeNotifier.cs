using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Circuit-local notification after acknowledged persistence; contains no wire or cached profile state.</summary>
internal sealed class UserProgressChangeNotifier
{
    private readonly ILogger<UserProgressChangeNotifier> _logger;
    public UserProgressChangeNotifier(ILogger<UserProgressChangeNotifier>? logger = null) => _logger = logger ?? NullLogger<UserProgressChangeNotifier>.Instance;
    public event Action<Guid, Guid>? Changed;
    public void Publish(Guid profileId, Guid assetId)
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<Guid, Guid>)subscriber)(profileId, assetId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Progress refresh subscriber failed for profile {ProfileId} and asset {AssetId}", profileId, assetId); }
        }
    }
}
