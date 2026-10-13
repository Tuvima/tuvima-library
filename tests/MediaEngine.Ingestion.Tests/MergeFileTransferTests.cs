using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The byte-checked copy behind a folder merge, on real temp files: nothing is overwritten, a copy that does not match is
/// discarded, an original is removed only when the copy still matches, and sidecars are copied or moved the same careful way.
/// </summary>
public sealed class MergeFileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_transfer_{Guid.NewGuid():N}");
    private readonly ScriptedHasher _hasher = new();
    private readonly MergeFileTransfer _transfer;

    public MergeFileTransferTests()
    {
        Directory.CreateDirectory(_root);
        _transfer = new MergeFileTransfer(_hasher, NullLogger<MergeFileTransfer>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by the next run's cleanup of tuvima_transfer_* folders.
            _ = ex;
        }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task A_copy_lands_at_the_wanted_name_with_the_same_bytes_and_time_and_leaves_no_temporary_file()
    {
        var source = Write("in/film.mkv", "film bytes");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 5, 17, 10, 0, 0, DateTimeKind.Utc));
        var target = Path.Combine(_root, "out", "Film (2020)", "Film (2020).mkv");

        var result = await _transfer.CopyVerifiedAsync(source, target);

        Assert.Equal(target, result.FinalPath);
        Assert.False(result.UniqueName);
        Assert.Equal("film bytes", File.ReadAllText(target));
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(target));
        Assert.Equal("film bytes", File.ReadAllText(source));
        Assert.Equal([target], Directory.EnumerateFiles(Path.Combine(_root, "out"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_name_that_is_taken_is_never_overwritten_and_the_copy_gets_a_numbered_name()
    {
        var source = Write("in/film.mkv", "new film");
        var target = Write("out/Film.mkv", "old film");

        var result = await _transfer.CopyVerifiedAsync(source, target);

        Assert.True(result.UniqueName);
        Assert.Equal(Path.Combine(_root, "out", "Film (2).mkv"), result.FinalPath);
        Assert.Equal("old film", File.ReadAllText(target));
        Assert.Equal("new film", File.ReadAllText(result.FinalPath!));
    }

    [Fact]
    public async Task A_copy_that_does_not_read_back_the_same_is_discarded_and_the_original_is_untouched()
    {
        var source = Write("in/film.mkv", "film bytes");
        var target = Path.Combine(_root, "out", "Film.mkv");
        _hasher.Override = path => path.EndsWith(MergeFileTransfer.PartSuffix, StringComparison.Ordinal) ? new string('0', 64) : null;

        var result = await _transfer.CopyVerifiedAsync(source, target);

        Assert.Null(result.FinalPath);
        Assert.Contains("did not match", result.Failure);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "out"), "*", SearchOption.AllDirectories));
        Assert.Equal("film bytes", File.ReadAllText(source));
    }

    [Fact]
    public async Task An_original_is_removed_only_while_the_copy_still_matches_it()
    {
        var source = Write("in/film.mkv", "film bytes");
        var copy = Write("out/Film.mkv", "film bytes");
        var different = Write("out/Other.mkv", "something else");

        Assert.False(await _transfer.DeleteOriginalIfCopyMatchesAsync(source, different));
        Assert.True(File.Exists(source));
        Assert.False(await _transfer.DeleteOriginalIfCopyMatchesAsync(source, Path.Combine(_root, "out", "Missing.mkv")));
        Assert.True(File.Exists(source));

        Assert.True(await _transfer.DeleteOriginalIfCopyMatchesAsync(source, copy));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public async Task Two_files_are_the_same_only_when_size_and_fingerprint_agree()
    {
        var a = Write("a.bin", "same");
        var b = Write("b.bin", "same");
        var c = Write("c.bin", "other!");

        Assert.True(await _transfer.SameContentAsync(a, b));
        Assert.False(await _transfer.SameContentAsync(a, c));
        Assert.False(await _transfer.SameContentAsync(a, Path.Combine(_root, "nope.bin")));
    }

    // ── Sidecars ─────────────────────────────────────────────────────────────────────────────────────────────────

    private SidecarMover Sidecars() => new(new SystemOrganiseFileMover(), NullLogger<SidecarMover>.Instance);

    [Fact]
    public void Copying_sidecars_keeps_the_originals_and_never_overwrites_a_different_file()
    {
        var media = Write("in/Dune.2021.mkv", "dune");
        Write("in/Dune.2021.en.srt", "english");
        Write("in/Dune.2021.nfo", "nfo");
        Write("in/readme.txt", "stays");
        var newMedia = Path.Combine(_root, "out", "Dune (2021).mkv");
        Write("out/Dune (2021).nfo", "a different nfo");

        var results = Sidecars().Transfer(media, newMedia, move: false, mayWrite: _ => true);

        Assert.Equal("english", File.ReadAllText(Path.Combine(_root, "out", "Dune (2021).en.srt")));
        Assert.Equal("a different nfo", File.ReadAllText(Path.Combine(_root, "out", "Dune (2021).nfo")));
        Assert.True(File.Exists(Path.Combine(_root, "in", "Dune.2021.en.srt")));
        Assert.True(File.Exists(Path.Combine(_root, "in", "readme.txt")));
        Assert.Contains(results, r => r.Outcome == MediaEngine.Domain.Contracts.FolderOrganiseOutcomes.SidecarCopied);
        Assert.Contains(results, r => r.Outcome == MediaEngine.Domain.Contracts.FolderOrganiseOutcomes.NotMoved);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(_root, "out")), path => path.EndsWith(MergeFileTransfer.PartSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public void Moving_sidecars_removes_each_original_only_after_its_copy_matches_and_only_when_allowed()
    {
        var media = Write("in/Dune.2021.mkv", "dune");
        var subtitles = Write("in/Dune.2021.en.srt", "english");
        var notes = Write("in/Dune.2021.nfo", "nfo");
        var newMedia = Path.Combine(_root, "out", "Dune (2021).mkv");

        var results = Sidecars().Transfer(
            media, newMedia, move: true, mayWrite: _ => true,
            mayRemove: path => !path.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));

        Assert.False(File.Exists(subtitles));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(Path.Combine(_root, "out", "Dune (2021).nfo")));
        Assert.Contains(results, r => r.Outcome == MediaEngine.Domain.Contracts.FolderOrganiseOutcomes.SidecarMoved);
        Assert.Contains(results, r => r.Outcome == MediaEngine.Domain.Contracts.FolderOrganiseOutcomes.SidecarCopied && r.Detail!.Contains("could not be removed"));
    }

    [Fact]
    public void A_sidecar_the_target_policy_does_not_allow_is_not_written()
    {
        var media = Write("in/Dune.2021.mkv", "dune");
        Write("in/Dune.2021.en.srt", "english");
        var newMedia = Path.Combine(_root, "out", "Dune (2021).mkv");

        var results = Sidecars().Transfer(media, newMedia, move: false, mayWrite: _ => false);

        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
        Assert.All(results, r => Assert.Equal(MediaEngine.Domain.Contracts.FolderOrganiseOutcomes.NotMoved, r.Outcome));
    }
}
