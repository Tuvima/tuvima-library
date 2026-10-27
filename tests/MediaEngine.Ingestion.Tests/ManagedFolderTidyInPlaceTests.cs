using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A managed folder that is not marked New media is kept organised where it is: its files are renamed inside it and never
/// carried into the New media folder (or any other folder, or onto another drive). Only a file still waiting in staging is
/// promoted into the New media folder, and an as-is folder is never touched.
/// </summary>
public sealed class ManagedFolderTidyInPlaceTests : IDisposable
{
    private readonly ImportFolderHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Write(string folder, string relativePath, string content = "media bytes")
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task A_file_in_a_managed_folder_that_is_not_New_media_is_renamed_inside_that_folder()
    {
        var source = Write(_h.ElsewhereDir, Path.Combine("downloads", "Dune.2021.1080p.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        var expected = Path.Combine(_h.ElsewhereDir, "Dune (2021)", "Dune (2021).mkv");
        Assert.True(File.Exists(expected));
        Assert.Equal("dune bytes", File.ReadAllText(expected));
        Assert.False(File.Exists(source));
        Assert.Equal(expected, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);
        Assert.Empty(_h.FilesUnder(_h.MoviesDir));
        Assert.Empty(_h.FilesUnder(_h.ImportDir));
    }

    [Fact]
    public async Task A_file_already_at_its_standard_name_in_a_managed_folder_stays_exactly_where_it_is()
    {
        var path = Write(_h.ElsewhereDir, Path.Combine("Dune (2021)", "Dune (2021).mkv"));
        var assetId = await _h.RegisterAsync(path, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.True(File.Exists(path));
        Assert.Equal(path, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);
        Assert.Empty(_h.FilesUnder(_h.MoviesDir));
    }

    [Fact]
    public async Task A_file_waiting_in_staging_is_still_promoted_into_the_New_media_folder()
    {
        var staged = Write(_h.MoviesDir, Path.Combine(".data", "staging", "dune.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(staged, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        var expected = Path.Combine(_h.MoviesDir, "Dune (2021)", "Dune (2021).mkv");
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(staged));
        Assert.Empty(_h.FilesUnder(_h.ElsewhereDir));
    }

    [Fact]
    public async Task A_file_in_an_as_is_folder_is_never_touched_or_moved_into_the_New_media_folder()
    {
        _h.Options.LibraryFolders =
        [
            LibraryFolderEntryFactory.Create(ImportFolderHarness.NewLibrary(
                ImportFolderHarness.MoviesLibraryId,
                "Movies",
                "Movies",
                destinationId: "movies-new",
                ImportFolderHarness.AsIs("movies-asis", _h.AsIsDir),
                ImportFolderHarness.Managed("movies-new", _h.MoviesDir))),
        ];
        var source = Write(_h.AsIsDir, "Dune.2021.1080p.mkv", "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.True(File.Exists(source));
        Assert.Equal(source, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);
        Assert.Empty(_h.FilesUnder(_h.MoviesDir));
    }

    [Fact]
    public async Task Leftovers_of_a_cancelled_conversion_are_tidied_in_their_own_folder_and_none_leave_it()
    {
        // Half the folder was already named by the stopped job; the rest still has its download names.
        var named = Write(_h.ElsewhereDir, Path.Combine("Alien (1979)", "Alien (1979).mkv"), "alien");
        var leftoverOne = Write(_h.ElsewhereDir, Path.Combine("incoming", "Dune.2021.mkv"), "dune");
        var leftoverTwo = Write(_h.ElsewhereDir, "Blade.Runner.1982.mkv", "blade runner");
        var alienId = await _h.RegisterAsync(named, MediaType.Movies, "Alien", "1979");
        var duneId = await _h.RegisterAsync(leftoverOne, MediaType.Movies, "Dune", "2021");
        var bladeId = await _h.RegisterAsync(leftoverTwo, MediaType.Movies, "Blade Runner", "1982");

        foreach (var id in new[] { alienId, duneId, bladeId })
        {
            await _h.Organizer.TryAutoOrganizeAsync(id);
        }

        Assert.Empty(_h.FilesUnder(_h.MoviesDir));
        Assert.Equal(
            [
                Path.Combine( "Alien (1979)", "Alien (1979).mkv"),
                Path.Combine("Blade Runner (1982)", "Blade Runner (1982).mkv"),
                Path.Combine( "Dune (2021)", "Dune (2021).mkv"),
            ],
            _h.FilesUnder(_h.ElsewhereDir).Select(path => Path.GetRelativePath(_h.ElsewhereDir, path)).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void A_new_name_outside_the_folder_the_file_lives_in_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "tuvima_root_check", "Movies");
        var inside = Path.Combine(root, "a", "x.mkv");
        var insideNew = Path.Combine(root, "Movies", "Dune", "x.mkv");
        var sibling = Path.Combine(Path.GetTempPath(), "tuvima_root_check", "Movies2", "x.mkv");
        var parentTraversal = Path.Combine(root, "..", "Elsewhere", "x.mkv");

        Assert.True(AutoOrganizeService.StaysInsideRoot(inside, insideNew, root));
        Assert.False(AutoOrganizeService.StaysInsideRoot(inside, sibling, root));
        Assert.False(AutoOrganizeService.StaysInsideRoot(inside, parentTraversal, root));
        Assert.False(AutoOrganizeService.StaysInsideRoot(sibling, insideNew, root));
    }

    [Fact]
    public async Task A_file_already_in_the_New_media_folder_outside_staging_is_still_renamed_in_place()
    {
        var source = Write(_h.MoviesDir, Path.Combine("downloads", "Dune.2021.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.True(File.Exists(Path.Combine(_h.MoviesDir, "Dune (2021)", "Dune (2021).mkv")));
        Assert.Empty(_h.FilesUnder(_h.ElsewhereDir));
    }

    [Fact]
    public async Task A_file_waiting_in_Review_is_not_renamed_in_place_until_a_person_confirms_it()
    {
        var source = Write(_h.ElsewhereDir, Path.Combine("downloads", "Dune.2021.mkv"), "dune bytes");
        var assetId = await _h.RegisterAsync(source, MediaType.Movies, "Dune", "2021");
        await _h.AddPendingReviewAsync(assetId);

        await _h.Organizer.TryAutoOrganizeAsync(assetId);

        Assert.True(File.Exists(source));
        Assert.Equal(source, (await _h.Assets.FindByIdAsync(assetId))!.FilePathRoot);
    }
}
