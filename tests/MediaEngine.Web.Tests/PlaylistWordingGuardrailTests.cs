namespace MediaEngine.Web.Tests;

/// <summary>
/// Issue 1205: the Listen playlist screens spelled "Playlist", "New Playlist" and "Add to playlist..." straight into the code, so they
/// stayed English for every household. These tests keep the word out of the Listen playlist files; it is read from
/// <c>SharedStrings.resx</c> with <c>L["Key"]</c> (or the localizer a service is given) like every other word on screen.
/// </summary>
public sealed class PlaylistWordingGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web");

    // The Listen playlist screens and the one flow every Add to playlist entry point shares (listen-playlists spec, work packets 1 to 7).
    private static readonly string[] FilePatterns =
    [
        "Components/Browse/MediaBrowseShell.State.cs",
        "Components/Listen/*",
        "Components/Pages/ListenPage*",
        "Components/Pages/ListenBrowse*",
        "Components/Player/PlaybackItemMenu*",
        "Components/Player/QueueSavePlaylist*",
        "Components/Details/PlaylistPickerDialog*",
        "Components/Details/PersonalStatusMenuItems*",
        "Components/Details/MediaMembershipDialog*",
        "Components/Collections/CollectionEditorShell*",
        "Services/Editing/Playlist*",
        "Services/Editing/SongDragDropService.cs",
        "Services/Editing/CollectionEditorLauncherService.cs",
        "Services/Playback/QueuePlaylistSaver.cs",
        "Services/Navigation/Listen*",
        "Services/Ui/ListenPlaylistOrder.cs",
    ];

    private static IEnumerable<string> ListenPlaylistFiles() =>
        FilePatterns
            .SelectMany(pattern =>
            {
                var folder = Path.Combine(WebRoot, Path.GetDirectoryName(pattern)!.Replace('/', Path.DirectorySeparatorChar));
                return Directory.EnumerateFiles(folder, Path.GetFileName(pattern), SearchOption.TopDirectoryOnly);
            })
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".razor", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    [Fact]
    public void TheListenPlaylistFiles_TypeNoPlaylistWordsIntoTheCode()
    {
        var files = ListenPlaylistFiles().ToList();
        Assert.True(files.Count > 40, "The guardrail lost track of the Listen playlist files.");

        var typed = files
            .SelectMany(path => PlaylistWordingScanner.Find(File.ReadAllText(path), razor: path.EndsWith(".razor", StringComparison.Ordinal))
                .Select(hit => $"{Path.GetRelativePath(RepoRoot, path).Replace('\\', '/')}:{hit.Line} \"{Trim(hit.Text)}\""))
            .ToList();

        Assert.True(typed.Count == 0,
            "Move this wording into SharedStrings.resx and show it with L[\"Area_Name\"] (use Common_AddToPlaylist, Glossary_Playlists and the PlaylistPage_ / PlaylistTiles_ keys where they fit; a collection type is CollectionTypeNames.Playlist):\n"
            + string.Join('\n', typed));
    }

    [Theory]
    [InlineData("var title = \"Playlist\";", false, true)]
    [InlineData("Label = \"New Smart Playlist\",", false, true)]
    [InlineData("Add(Key, \"Add to playlist…\", Glyph);", false, true)]
    [InlineData("var line = $\"{label} Playlist\";", false, true)]
    [InlineData("var line = $\"{(smart ? \"Smart\" : \"Hand-made\")} playlists\";", false, true)]
    [InlineData("<AppMenuItem Label=\"Add to playlist\" />", true, true)]
    [InlineData("<AppMenuItem Title=\"@($\"Open {name} playlist\")\" />", true, true)]
    [InlineData("<h2>Playlists</h2>", true, true)]
    [InlineData("var route = \"/listen/music/playlists/system/favorite-songs\";", false, false)]
    [InlineData("var key = L[\"PlaylistPage_Kind\"].Value;", false, false)]
    [InlineData("var type = CollectionConstants.Playlist;", false, false)]
    [InlineData("// A \"Playlist\" in a comment is not shown.", false, false)]
    [InlineData("Logger.LogWarning(ex, \"Could not load Listen playlist shortcuts.\");", false, false)]
    [InlineData("<div class=\"playlist-picker__row is-selected\" data-song-drop=\"@(drop ? \"playlist\" : null)\">", true, false)]
    [InlineData("<div class=\"listen-rail__playlist-row @(playlist.Id == dragging ? \"is-dragging\" : null)\"></div>", true, false)]
    [InlineData("@* The Add to playlist picker *@", true, false)]
    public void ThePlaylistWordScanner_FlagsTypedWordsAndLeavesNamesAndKeysAlone(string source, bool razor, bool flagged)
    {
        Assert.Equal(flagged, PlaylistWordingScanner.Find(source, razor).Count > 0);
    }

    private static string Trim(string text) => text.Length <= 60 ? text : text[..57] + "...";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
