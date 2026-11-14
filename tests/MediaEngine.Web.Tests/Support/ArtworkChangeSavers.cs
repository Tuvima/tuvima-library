using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// What the editor host gives the Artwork tool in the app: a picture change made through the Engine's edit session on a list of its own
/// (check, then save), so a test can drive the picker without a host.
/// </summary>
internal static class ArtworkChangeSavers
{
    public static ArtworkChangeSaver For(IEngineApiClient api) => choices =>
    {
        var pending = new PendingEditList();
        foreach (var choice in choices)
        {
            pending.SetArtwork(choice.EntityId, choice.Slot, choice.TargetId);
        }

        return new EditSessionSaver(api).SaveAsync(pending);
    };
}
