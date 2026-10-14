using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Hydration tidies a file's name and carries its companions along, but never deletes or overwrites a user's file by name
/// alone: a companion whose new name is taken stays where it is (even when identical), folder-wide files wait for the last
/// media file to leave, and two files that want one standard name each keep their own file and their own data store row.
/// </summary>
public sealed class HydrationDataSafetyTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private FolderConversionHarness NewConversionHarness()
    {
        var harness = new FolderConversionHarness(LibraryDuplicatePolicies.KeepOne);
        _disposables.Add(harness);
        return harness;
    }

    private static string StandardMovie(FolderConversionHarness h) =>
        Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "Dune (2021).mkv");

    // ── HIGH 1: companions are never deleted by name ─────────────────────────

    [Fact]
    public async Task A_Calibre_metadata_file_survives_when_the_new_folder_already_has_a_different_one()
    {
        var h = NewConversionHarness();
        var source = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "dune");
        var calibre = h.Write(Path.Combine("downloads", "metadata.opf"), "calibre opf");
        var targetOpf = h.Write(Path.Combine("Movies", "Dune (2021)", "metadata.opf"), "target opf");
        var id = await h.RegisterAsync(source, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(id);

        Assert.Equal("dune", File.ReadAllText(StandardMovie(h)));
        Assert.Equal("calibre opf", File.ReadAllText(calibre));
        Assert.Equal("target opf", File.ReadAllText(targetOpf));
    }

    [Fact]
    public async Task An_identical_companion_whose_new_name_is_taken_is_also_kept_not_removed()
    {
        var h = NewConversionHarness();
        var source = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "dune");
        var cover = h.Write(Path.Combine("downloads", "cover.jpg"), "same cover");
        var metadata = h.Write(Path.Combine("downloads", "metadata.json"), "same json");
        var targetCover = h.Write(Path.Combine("Movies", "Dune (2021)", "cover.jpg"), "same cover");
        var targetMetadata = h.Write(Path.Combine("Movies", "Dune (2021)", "metadata.json"), "same json");
        var id = await h.RegisterAsync(source, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(id);

        Assert.True(File.Exists(StandardMovie(h)));
        Assert.True(File.Exists(cover));
        Assert.True(File.Exists(metadata));
        Assert.True(File.Exists(targetCover));
        Assert.True(File.Exists(targetMetadata));
    }

    [Fact]
    public async Task A_folder_level_cover_stays_while_other_media_remain_in_the_old_folder()
    {
        var h = NewConversionHarness();
        var source = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "dune");
        var neighbour = h.Write(Path.Combine("downloads", "Alien.1979.mkv"), "alien");
        var cover = h.Write(Path.Combine("downloads", "cover.jpg"), "shared cover");
        var id = await h.RegisterAsync(source, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(id);

        Assert.True(File.Exists(StandardMovie(h)));
        Assert.True(File.Exists(neighbour));
        Assert.True(File.Exists(cover));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(StandardMovie(h))!, "cover.jpg")));
    }

    [Fact]
    public async Task Companions_follow_the_last_media_file_out_of_the_old_folder()
    {
        var h = NewConversionHarness();
        var source = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "dune");
        h.Write(Path.Combine("downloads", "Dune.2021.en.srt"), "subtitles");
        h.Write(Path.Combine("downloads", "cover.jpg"), "cover");
        h.Write(Path.Combine("downloads", "poster-0f3a9c.jpg"), "poster variant");
        var id = await h.RegisterAsync(source, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(id);

        var folder = Path.GetDirectoryName(StandardMovie(h))!;
        Assert.Equal("subtitles", File.ReadAllText(Path.Combine(folder, "Dune (2021).en.srt")));
        Assert.Equal("cover", File.ReadAllText(Path.Combine(folder, "cover.jpg")));
        Assert.Equal("poster variant", File.ReadAllText(Path.Combine(folder, "poster-0f3a9c.jpg")));
        Assert.False(Directory.Exists(Path.Combine(h.SourceDir, "downloads")));
    }

    // ── Artwork takes the name its new folder expects ────────────────────────

    [Fact]
    public async Task A_poster_moving_from_its_own_folder_into_a_shared_folder_is_named_after_the_file_and_leaves_the_folders_poster_alone()
    {
        var h = NewConversionHarness();
        var source = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "dune");
        var sourcePoster = h.Write(Path.Combine("downloads", "poster.jpg"), "dune poster");
        h.Write(Path.Combine("Movies", "Dune (2021)", "Dune Extras.mkv"), "extras");
        var folderPoster = h.Write(Path.Combine("Movies", "Dune (2021)", "poster.jpg"), "extras poster");
        var id = await h.RegisterAsync(source, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(id);

        var folder = Path.GetDirectoryName(StandardMovie(h))!;
        Assert.True(File.Exists(StandardMovie(h)));
        Assert.Equal("dune poster", File.ReadAllText(Path.Combine(folder, "Dune (2021)-poster.jpg")));
        Assert.Equal("extras poster", File.ReadAllText(folderPoster));
        Assert.False(File.Exists(sourcePoster));
    }

    [Fact]
    public async Task A_poster_named_after_the_file_becomes_the_folder_poster_when_the_file_moves_to_a_folder_of_its_own()
    {
        var h = NewConversionHarness();
        var source = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "dune");
        var sourcePoster = h.Write(Path.Combine("downloads", "Dune.2021-poster.jpg"), "dune poster");
        h.Write(Path.Combine("downloads", "Alien.1979.mkv"), "alien");
        var neighbourPoster = h.Write(Path.Combine("downloads", "Alien.1979-poster.jpg"), "alien poster");
        var id = await h.RegisterAsync(source, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(id);

        var folder = Path.GetDirectoryName(StandardMovie(h))!;
        Assert.Equal("dune poster", File.ReadAllText(Path.Combine(folder, "poster.jpg")));
        Assert.False(File.Exists(Path.Combine(folder, "Dune (2021)-poster.jpg")));
        Assert.False(File.Exists(sourcePoster));
        Assert.Equal("alien poster", File.ReadAllText(neighbourPoster));
    }

    [Fact]
    public async Task A_poster_whose_new_name_is_taken_stays_where_it_is()
    {
        var h = NewConversionHarness();
        var source = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "dune");
        var sourcePoster = h.Write(Path.Combine("downloads", "poster.jpg"), "dune poster");
        h.Write(Path.Combine("Movies", "Dune (2021)", "Dune Extras.mkv"), "extras");
        var taken = h.Write(Path.Combine("Movies", "Dune (2021)", "Dune (2021)-poster.jpg"), "someone else's poster");
        var id = await h.RegisterAsync(source, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(id);

        var folder = Path.GetDirectoryName(StandardMovie(h))!;
        Assert.True(File.Exists(StandardMovie(h)));
        Assert.Equal("dune poster", File.ReadAllText(sourcePoster));
        Assert.Equal("someone else's poster", File.ReadAllText(taken));
        Assert.False(File.Exists(Path.Combine(folder, "poster.jpg")), "it never becomes the shared folder's poster");
    }

    // ── HIGH 2: the data store follows the file that really moved ────────────

    [Fact]
    public async Task Two_editions_that_share_one_standard_name_each_keep_their_own_file_and_row()
    {
        var h = NewConversionHarness();
        var first = h.Write(Path.Combine("Movies", "Dune (2021)", "Dune (2021).mkv"), "1080p bytes");
        var second = h.Write(Path.Combine("downloads", "Dune.2021.2160p.mkv"), "4k bytes");
        var firstId = await h.RegisterAsync(first, "Dune", "2021");
        var secondId = await h.RegisterAsync(second, "Dune", "2021");

        await h.Auto.TryAutoOrganizeAsync(secondId);
        await h.Auto.TryAutoOrganizeAsync(firstId);

        // The second edition was not renamed onto (or beside) the first one's name, so nothing can later mistake one for the other.
        Assert.Equal(first, (await h.Assets.FindByIdAsync(firstId))!.FilePathRoot);
        Assert.Equal(second, (await h.Assets.FindByIdAsync(secondId))!.FilePathRoot);
        Assert.Equal("1080p bytes", File.ReadAllText(first));
        Assert.Equal("4k bytes", File.ReadAllText(second));
        Assert.Equal(["Dune (2021).mkv"], Directory.EnumerateFiles(Path.GetDirectoryName(first)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_staged_file_whose_standard_name_is_taken_is_recorded_under_the_numbered_name_it_really_got()
    {
        var h = new ImportFolderHarness();
        _disposables.Add(h);
        var occupant = Path.Combine(h.MoviesDir, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(occupant)!);
        File.WriteAllText(occupant, "1080p bytes");
        var occupantId = await h.RegisterAsync(occupant, MediaType.Movies, "Dune", "2021");
        var staged = Path.Combine(h.MoviesDir, ".data", "staging", "dune.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "4k bytes");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(staged)!, "dune.en.srt"), "subtitles");
        var stagedId = await h.RegisterAsync(staged, MediaType.Movies, "Dune", "2021");

        await h.Organizer.TryAutoOrganizeAsync(stagedId);

        var folder = Path.GetDirectoryName(occupant)!;
        var numbered = Path.Combine(folder, "Dune (2021) (2).mkv");
        Assert.Equal(numbered, (await h.Assets.FindByIdAsync(stagedId))!.FilePathRoot);
        Assert.Equal(occupant, (await h.Assets.FindByIdAsync(occupantId))!.FilePathRoot);
        Assert.Equal("4k bytes", File.ReadAllText(numbered));
        Assert.Equal("1080p bytes", File.ReadAllText(occupant));

        // The files that travel with it follow the name it really got, not the occupant's.
        Assert.Equal("subtitles", File.ReadAllText(Path.Combine(folder, "Dune (2021) (2).en.srt")));
        Assert.False(File.Exists(Path.Combine(folder, "Dune (2021).en.srt")));
    }

    [Fact]
    public async Task The_organizer_reports_the_path_it_really_used_and_nothing_when_it_did_not_move()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima_move_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);
            var one = Path.Combine(root, "one.mkv");
            var two = Path.Combine(root, "two.mkv");
            var wanted = Path.Combine(root, "sub", "Name.mkv");
            File.WriteAllText(one, "one");
            File.WriteAllText(two, "two");

            Assert.Equal(wanted, await organizer.ExecuteMoveAsync(one, wanted));
            var second = await organizer.ExecuteMoveAsync(two, wanted);

            Assert.Equal(Path.Combine(root, "sub", "Name (2).mkv"), second);
            Assert.Equal("one", File.ReadAllText(wanted));
            Assert.Equal("two", File.ReadAllText(second!));
            Assert.Null(await organizer.ExecuteMoveAsync(Path.Combine(root, "missing.mkv"), Path.Combine(root, "x.mkv")));
            Assert.Equal(wanted, await organizer.ExecuteMoveAsync(wanted, wanted));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── MEDIUM 3: restart recovery on a drive that ignores letter case ───────

    private static bool DriveIgnoresCase(string folder)
    {
        var probe = Path.Combine(folder, $"probe-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "probe");
        try
        {
            return File.Exists(Path.Combine(folder, Path.GetFileName(probe).ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    [Fact]
    public async Task A_sidecar_rename_stopped_after_the_media_files_case_rename_finished_is_completed_on_restart()
    {
        var h = NewConversionHarness();
        if (!DriveIgnoresCase(h.SourceDir))
        {
            return; // A drive that keeps letter case apart never shows both spellings at once.
        }

        h.Mover.SameFile = (first, second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        var folder = Path.Combine("Movies", "Dune (2021)");
        var media = h.Write(Path.Combine(folder, "dune (2021).mkv"), "the only copy");
        h.Write(Path.Combine(folder, "dune (2021).en.srt"), "subtitles");
        var id = await h.RegisterAsync(media, "Dune", "2021");
        h.Mover.AfterMove = (from, to) =>
        {
            if (from.EndsWith(".en.srt", StringComparison.Ordinal) && to.EndsWith(CaseSafeRename.TemporarySuffix, StringComparison.Ordinal))
            {
                throw new SimulatedCrashException();
            }
        };
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());
        var directory = Path.Combine(h.SourceDir, folder);
        Assert.Contains(Directory.EnumerateFiles(directory), file => file.EndsWith(CaseSafeRename.TemporarySuffix, StringComparison.Ordinal));

        h.Mover.AfterMove = null;
        var result = await h.RunAsync();

        Assert.Equal(0, result.Failed);
        var names = Directory.EnumerateFiles(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(["Dune (2021).en.srt", "Dune (2021).mkv"], names);
        Assert.Equal("subtitles", File.ReadAllText(Path.Combine(directory, "Dune (2021).en.srt")));
        Assert.Equal(Path.Combine(directory, "Dune (2021).mkv"), (await h.Assets.FindByIdAsync(id))!.FilePathRoot);
    }

    [Fact]
    public async Task A_case_rename_stopped_just_before_the_data_store_update_is_completed_on_restart()
    {
        var h = NewConversionHarness();
        if (!DriveIgnoresCase(h.SourceDir))
        {
            return; // A drive that keeps letter case apart never shows both spellings at once.
        }

        h.Mover.SameFile = (first, second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        var folder = Path.Combine("Movies", "Dune (2021)");
        var media = h.Write(Path.Combine(folder, "dune (2021).mkv"), "the only copy");
        var id = await h.RegisterAsync(media, "Dune", "2021");

        // Stop right after the second step: the file carries its new name, the data store still holds the old spelling.
        h.Mover.AfterMove = (_, to) =>
        {
            if (string.Equals(Path.GetFileName(to), "Dune (2021).mkv", StringComparison.Ordinal))
            {
                throw new SimulatedCrashException();
            }
        };
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());
        Assert.Equal(media, (await h.Assets.FindByIdAsync(id))!.FilePathRoot);

        h.Mover.AfterMove = null;
        var result = await h.RunAsync();

        Assert.Equal(0, result.Failed);
        var directory = Path.Combine(h.SourceDir, folder);
        Assert.Equal(["Dune (2021).mkv"], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
        Assert.Equal(Path.Combine(directory, "Dune (2021).mkv"), (await h.Assets.FindByIdAsync(id))!.FilePathRoot);
    }
}
