using System.Reflection;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A title moves into another album, season, series or audiobook only through the Match step (details overlay spec D12). The
/// old music-track move, the save-time membership move and its "Save and Move" footer are gone and must not come back; the
/// retail rematch keeps its own "Confirm move" and the membership preview it depends on.
/// </summary>
public sealed class LibraryMoveRetiredGuardrailTests
{
    private static readonly string SrcRoot = Path.Combine(FindRepoRoot(), "src");

    private static readonly string[] RetiredTokens =
    [
        "Review exact release tracks",
        "Save and Move",
        "Save and move",
        "music-track-move",
        "membership-apply",
        "membership-suggestions",
        "MusicTrackMove",
        "SaveMusicTrackMove",
        "ApplyMediaEditorMembership",
        "GetMediaEditorMembershipSuggestions",
        "ConfirmMembershipMove",
        // A parallel implementation (#340, #345 and its follow-up) was retired in favour of the one move service and batch
        // section (Shy, 2026-10-20); a later merge must not bring it back.
        "ItemMoveService",
        "ItemMoveRepository",
        "ItemMovePanel",
        "ItemBatchMovePanel",
        "EditingBatchSection",
        "EngineApiClient.ItemMove",
        "PreviewItemMove",
    ];

    [Fact]
    public void TheEngineClientHasNoOldMoveOrMembershipApplyMethods()
    {
        var names = typeof(IEngineApiClient).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(method => method.Name).ToHashSet();

        Assert.DoesNotContain("PreviewMusicTrackMoveAsync", names);
        Assert.DoesNotContain("SaveMusicTrackMoveAsync", names);
        Assert.DoesNotContain("ApplyMediaEditorMembershipAsync", names);
        Assert.DoesNotContain("GetMediaEditorMembershipSuggestionsAsync", names);
    }

    [Fact]
    public void TheEngineClientKeepsTheMembershipPreviewTheRetailRematchNeeds_AndHasTheNewMoveActions()
    {
        var names = typeof(IEngineApiClient).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(method => method.Name).ToHashSet();

        Assert.Contains("PreviewMediaEditorMembershipAsync", names);
        Assert.Contains("GetLibraryMoveDestinationsAsync", names);
        Assert.Contains("PreviewLibraryMoveAsync", names);
        Assert.Contains("MoveLibraryItemsAsync", names);
    }

    [Fact]
    public void NoSourceFileMentionsTheRetiredMovePaths()
    {
        var offenders = Directory.EnumerateFiles(SrcRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".cs", ".razor", ".css", ".js" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !IsSkipped(path))
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .SelectMany(file => RetiredTokens
                .Where(token => file.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(SrcRoot, file.Path).Replace('\\', '/')}: {token}"))
            .ToList();

        Assert.True(offenders.Count == 0, "The old move paths are retired (spec D12); moves use the Match step: " + string.Join("; ", offenders));
    }

    private static bool IsSkipped(string path) =>
        Path.GetRelativePath(SrcRoot, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or "node_modules" or "lib");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
