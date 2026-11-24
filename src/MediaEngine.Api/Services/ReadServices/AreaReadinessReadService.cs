using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Why an area may be empty (honest-waiting-messages WP1): setup state, the area's libraries and their folders, and where their files
/// stand. Read-only; the Dashboard asks only when a page has nothing to show or on Home. An administrator gets the facts and the one
/// reason picked in the order of spec D1; a member or Kids profile gets no facts at all (D6). A Vault library never counts.
/// </summary>
public sealed class AreaReadinessReadService(
    IConfigurationLoader configuration,
    OnboardingRepository onboarding,
    IAreaReadinessReadRepository files)
{
    public static bool IsKnownArea(string area) =>
        area is AreaReadinessAreas.Home or AreaReadinessAreas.Watch or AreaReadinessAreas.Read or AreaReadinessAreas.Listen;

    /// <summary>The answer a member or Kids profile gets: no counts, names or flags.</summary>
    public static AreaReadinessDto ForNonAdministrator(string area, bool kids) => new(
        area,
        kids ? AreaReadinessAudiences.Kids : AreaReadinessAudiences.Member,
        AreaReadinessReasons.Hidden,
        null, 0, [], 0, 0, 0, 0, 0, 0, []);

    public async Task<AreaReadinessDto> GetAdministratorAsync(string area, CancellationToken ct = default)
    {
        var setupFinished = string.Equals(onboarding.Get().State, "complete", StringComparison.Ordinal);
        var libraries = configuration.LoadLibraries().Libraries
            .Where(library => !library.IsVault && InArea(area, library.Area) && Guid.TryParse(library.Id, out _))
            .ToList();

        var unreachable = libraries
            .SelectMany(library => library.Sources
                .Where(source => !string.IsNullOrWhiteSpace(source.Path) && !Directory.Exists(source.Path))
                .Select(source => string.IsNullOrWhiteSpace(source.Label) ? library.Name : source.Label!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var counts = (await files.GetFileStageCountsAsync(ct).ConfigureAwait(false))
            .ToDictionary(count => count.LibraryId, StringComparer.OrdinalIgnoreCase);
        var byType = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var totals = new int[5];
        foreach (var library in libraries)
        {
            if (!counts.TryGetValue(Guid.Parse(library.Id).ToString("D"), out var count))
            {
                continue;
            }

            var values = new[] { count.Found, count.InProgress, count.WaitingForSource, count.InReview, count.Ready };
            var type = library.MediaTypes.FirstOrDefault() ?? "Unknown";
            byType.TryAdd(type, new int[5]);
            for (var i = 0; i < values.Length; i++)
            {
                totals[i] += values[i];
                byType[type][i] += values[i];
            }
        }

        var scanning = await files.GetOpenIntakeCountAsync(ct).ConfigureAwait(false);
        var reason = PickReason(setupFinished, libraries.Count, unreachable.Count, totals[0], totals[1], totals[2], totals[3], scanning);
        var types = byType
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new AreaReadinessMediaTypeDto(pair.Key, pair.Value[0], pair.Value[1], pair.Value[2], pair.Value[3], pair.Value[4]))
            .ToList();
        return new AreaReadinessDto(
            area, AreaReadinessAudiences.Administrator, reason, setupFinished, libraries.Count, unreachable,
            totals[0], totals[1], totals[2], totals[3], totals[4], scanning, types);
    }

    /// <summary>Spec D1: the first reason that applies, cause before effect.</summary>
    public static string PickReason(
        bool setupFinished, int libraryCount, int unreachableFolders, int found, int inProgress, int waitingForSource, int inReview, int scanning)
    {
        if (!setupFinished)
        {
            return AreaReadinessReasons.SetupUnfinished;
        }

        if (libraryCount == 0)
        {
            return AreaReadinessReasons.NoLibrary;
        }

        if (unreachableFolders > 0)
        {
            return AreaReadinessReasons.FolderUnreachable;
        }

        if (inProgress > 0 || scanning > 0)
        {
            return AreaReadinessReasons.StillLooking;
        }

        if (waitingForSource > 0)
        {
            return AreaReadinessReasons.WaitingForSource;
        }

        if (inReview > 0)
        {
            return AreaReadinessReasons.WaitingInReview;
        }

        return found == 0 ? AreaReadinessReasons.NothingFound : AreaReadinessReasons.Ready;
    }

    // Home is every area a person browses; Vault and View libraries are never part of it.
    private static bool InArea(string area, string libraryArea) => area == AreaReadinessAreas.Home
        ? libraryArea is LibraryAreas.Watch or LibraryAreas.Read or LibraryAreas.Listen
        : string.Equals(area, libraryArea, StringComparison.OrdinalIgnoreCase);
}
