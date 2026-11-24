using MediaEngine.Contracts.Library;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Components.MediaHub;

/// <summary>One button on an empty-area message: its words, where it goes, and whether it is the main action.</summary>
public sealed record AreaEmptyAction(string Label, string Href, bool Primary);

/// <summary>What an empty area page says: a title, one sentence, up to two actions and, optionally, one line pointing at what does work (honest-waiting-messages D3, D4).</summary>
public sealed record AreaEmptyMessage(
    string Title,
    string? Message,
    IReadOnlyList<AreaEmptyAction> Actions,
    string? ExtraLine = null,
    string? ExtraHref = null);

/// <summary>
/// Turns the Engine's area summary into the words of an empty page (honest-waiting-messages WP2, D1 to D9). The Engine has already
/// picked the reason in the order of D1; this decides only the wording, the up-to-two actions and the "something else is ready" line.
/// A member sees one plain sentence and no counts or links; a Kids profile sees the title alone (D6); a television shows the sentence
/// only (no buttons).
/// </summary>
public sealed class AreaEmptyMessagePresenter(IStringLocalizer<SharedStrings> text)
{
    public const string SetupRoute = "/setup";
    public const string LibrariesRoute = "/settings/libraries";
    public const string ProgressRoute = "/settings/general/library-activity";
    public const string PluginsRoute = "/settings/plugins";
    public const string ReviewRoute = "/settings/review";

    public AreaEmptyMessage Build(AreaReadinessDto readiness, bool television = false)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        var message = readiness.Audience switch
        {
            AreaReadinessAudiences.Kids => new AreaEmptyMessage(Text("AreaEmpty_MemberTitle"), null, []),
            AreaReadinessAudiences.Administrator => ForAdministrator(readiness),
            _ => new AreaEmptyMessage(Text("AreaEmpty_MemberTitle"), Text("AreaEmpty_MemberMessage"), []),
        };
        return television ? message with { Actions = [], ExtraHref = null } : message;
    }

    private AreaEmptyMessage ForAdministrator(AreaReadinessDto r)
    {
        var area = r.Area;
        switch (r.Reason)
        {
            case AreaReadinessReasons.SetupUnfinished:
                return new(Text("AreaEmpty_SetupTitle"), Text("AreaEmpty_SetupMessage"),
                    [new(Text("AreaEmpty_ContinueSetup"), SetupRoute, true)]);

            case AreaReadinessReasons.NoLibrary:
                return new(
                    area == AreaReadinessAreas.Home
                        ? Text("AreaEmpty_HomeNoLibraryTitle")
                        : Text("AreaEmpty_NoLibraryTitle", Verb(area)),
                    Text("AreaEmpty_NoLibraryMessage", Kinds(area)),
                    [new(Text("Common_AddALibrary"), LibrariesRoute, true)]);

            case AreaReadinessReasons.FolderUnreachable:
                return new(
                    Text("AreaEmpty_FolderTitle", r.UnreachableFolders.FirstOrDefault() ?? Text("AreaEmpty_FolderFallback")),
                    Text("AreaEmpty_FolderMessage"),
                    [new(Text("AreaEmpty_OpenLibraries"), LibrariesRoute, true)]);

            case AreaReadinessReasons.StillLooking:
                var working = Math.Max(r.FilesInProgress + r.FilesWaitingForSource + r.FilesInReview, r.FilesBeingScanned);
                return new(
                    Text("AreaEmpty_LookingTitle", Files(Math.Max(working, 1))),
                    Text("AreaEmpty_LookingMessage"),
                    [new(Text("AreaEmpty_SeeProgress"), ProgressRoute, false)]);

            case AreaReadinessReasons.WaitingForSource:
                return new(
                    Text("AreaEmpty_WaitingTitle", Counted(r.FilesWaitingForSource, area)),
                    Text("AreaEmpty_WaitingMessage", Capitalise(KindsNeedingSource(area))),
                    [
                        new(Text("AreaEmpty_ConnectSource"), PluginsRoute, true),
                        new(Text("AreaEmpty_SeeFiles", Files(r.FilesWaitingForSource)), ReviewRoute, false),
                    ],
                    ReadyElsewhere(r), ReadyElsewhereRoute(r));

            case AreaReadinessReasons.WaitingInReview:
                return new(
                    Text("AreaEmpty_ReviewTitle", Counted(r.FilesInReview, area)),
                    Text("AreaEmpty_ReviewMessage"),
                    [new(Text("AreaEmpty_OpenReview"), ReviewRoute, true)],
                    ReadyElsewhere(r), ReadyElsewhereRoute(r));

            default:
                // Nothing found, or files that are ready but sit in another tab of this area.
                return new(
                    r.FilesFound == 0
                        ? Text("AreaEmpty_NothingTitle", Noun(area))
                        : Text("AreaEmpty_MemberTitle"),
                    r.FilesFound == 0 ? Text("AreaEmpty_NothingMessage") : null,
                    [new(Text("AreaEmpty_OpenLibraries"), LibrariesRoute, true)],
                    ReadyElsewhere(r), ReadyElsewhereRoute(r));
        }
    }

    /// <summary>D4: personal videos need no source, so when some are ready say so and where they are.</summary>
    private string? ReadyElsewhere(AreaReadinessDto r)
    {
        var videos = r.MediaTypes.FirstOrDefault(type => string.Equals(type.MediaType, "Videos", StringComparison.OrdinalIgnoreCase));
        return r.Area is AreaReadinessAreas.Watch or AreaReadinessAreas.Home && videos is { FilesReady: > 0 }
            ? Text("AreaEmpty_ElsewhereLine", Counted(videos.FilesReady, AreaReadinessAreas.Watch, personal: true), Text("AreaEmpty_VideosTab"))
            : null;
    }

    private static string? ReadyElsewhereRoute(AreaReadinessDto r) =>
        r.MediaTypes.Any(type => string.Equals(type.MediaType, "Videos", StringComparison.OrdinalIgnoreCase) && type.FilesReady > 0)
            ? "/watch/videos"
            : null;

    private string Text(string key, params object[] args) => text[key, args].Value;

    private string Files(int count) => count == 1 ? Text("AreaEmpty_OneFile") : Text("AreaEmpty_ManyFiles", count);

    // "19 videos", "1 book or comic": the number and the area's own word for what was found (D9).
    private string Counted(int count, string area, bool personal = false)
    {
        if (personal)
        {
            return count == 1 ? Text("AreaEmpty_OnePersonalVideo") : Text("AreaEmpty_ManyPersonalVideos", count);
        }

        return count == 1 ? Text("AreaEmpty_OneFile") : Text("AreaEmpty_Counted", count, Noun(area));
    }

    private string Noun(string area) => Text(area switch
    {
        AreaReadinessAreas.Watch => "AreaEmpty_NounWatch",
        AreaReadinessAreas.Read => "AreaEmpty_NounRead",
        AreaReadinessAreas.Listen => "AreaEmpty_NounListen",
        _ => "AreaEmpty_NounHome",
    });

    private string Verb(string area) => Text(area switch
    {
        AreaReadinessAreas.Read => "AreaEmpty_VerbRead",
        AreaReadinessAreas.Listen => "AreaEmpty_VerbListen",
        _ => "AreaEmpty_VerbWatch",
    });

    private string Kinds(string area) => Text(area switch
    {
        AreaReadinessAreas.Watch => "AreaEmpty_KindsWatch",
        AreaReadinessAreas.Read => "AreaEmpty_KindsRead",
        AreaReadinessAreas.Listen => "AreaEmpty_KindsListen",
        _ => "AreaEmpty_KindsHome",
    });

    private string KindsNeedingSource(string area) => Text(area switch
    {
        AreaReadinessAreas.Watch => "AreaEmpty_NeedSourceWatch",
        AreaReadinessAreas.Read => "AreaEmpty_NeedSourceRead",
        AreaReadinessAreas.Listen => "AreaEmpty_NeedSourceListen",
        _ => "AreaEmpty_NeedSourceHome",
    });

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
