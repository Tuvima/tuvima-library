using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A name that differs only in letter case, or two names for one file, must never be mistaken for a duplicate: removing "the
/// copy" would remove the only file. On a drive that ignores case (SMB and CIFS shares, exFAT, macOS) the new name already
/// "exists" because it is the same file, so these tests stand in for such a drive with a mover that reports one file.
/// </summary>
public sealed class CaseOnlyRenameSafetyTests : IDisposable
{
    private readonly List<FolderConversionHarness> _harnesses = [];
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"tuvima_identity_{Guid.NewGuid():N}");

    public CaseOnlyRenameSafetyTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }

        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temp folder is removed by the next run's cleanup of tuvima_identity_* folders.
            _ = ex;
        }
    }

    private FolderConversionHarness NewHarness(string duplicatePolicy = LibraryDuplicatePolicies.KeepOne)
    {
        var harness = new FolderConversionHarness(duplicatePolicy);
        _harnesses.Add(harness);
        return harness;
    }

    [Fact]
    public async Task A_letter_case_only_change_is_a_rename_through_a_temporary_name_never_a_duplicate()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        h.Mover.SameFile = (first, second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        var path = h.Write(Path.Combine("Movies", "Dune (2021)", "dune (2021).mkv"), "the only copy");
        await h.RegisterAsync(path, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Count(FolderOrganiseOutcomes.DuplicateRemoved));
        var survivor = Directory.EnumerateFiles(Path.Combine(h.SourceDir, "Movies", "Dune (2021)")).Single();
        Assert.Equal("Dune (2021).mkv", Path.GetFileName(survivor));
        Assert.Equal("the only copy", File.ReadAllText(survivor));

        // Two steps through a temporary name, so it is the same safe path on every system.
        Assert.Equal(2, h.Mover.Moves.Count);
        Assert.EndsWith(CaseSafeRename.TemporarySuffix, h.Mover.Moves[0].To, StringComparison.Ordinal);
        Assert.Equal(Path.GetFileName(survivor), Path.GetFileName(h.Mover.Moves[1].To));
    }

    [Fact]
    public async Task Two_names_for_one_file_are_never_removed_as_a_duplicate()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        h.Mover.SameFile = (_, _) => true;
        var kept = h.Write(Path.Combine("Movies", "Dune (2021)", "Dune (2021).mkv"), "same bytes");
        var other = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "same bytes");
        await h.RegisterAsync(other, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(0, result.Count(FolderOrganiseOutcomes.DuplicateRemoved));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task A_true_duplicate_a_different_file_with_the_same_bytes_is_still_removed_under_Keep_one()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        var kept = h.Write(Path.Combine("Movies", "Dune (2021)", "Dune (2021).mkv"), "same bytes");
        var extra = h.Write(Path.Combine("downloads", "Dune.2021.mkv"), "same bytes");
        await h.RegisterAsync(extra, "Dune", "2021");

        var result = await h.RunAsync();

        Assert.Equal(1, result.Count(FolderOrganiseOutcomes.DuplicateRemoved));
        Assert.False(File.Exists(extra));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void A_sidecar_whose_name_only_changes_in_letter_case_is_renamed_not_removed()
    {
        var mover = new ScriptedMover { SameFile = (first, second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase) };
        var sidecars = new SidecarMover(mover, NullLogger<SidecarMover>.Instance);
        var oldMedia = Path.Combine(_scratch, "dune.mkv");
        var newMedia = Path.Combine(_scratch, "Dune.mkv");
        File.WriteAllText(oldMedia, "media");
        File.WriteAllText(Path.Combine(_scratch, "dune.srt"), "subtitles");

        var results = sidecars.Move(oldMedia, newMedia, (_, _) => true, mayDelete: _ => true);

        Assert.Single(results);
        Assert.Equal(FolderOrganiseOutcomes.SidecarMoved, results[0].Outcome);
        var names = Directory.EnumerateFiles(_scratch, "*.srt").Select(Path.GetFileName).ToList();
        Assert.Equal(["Dune.srt"], names);
        Assert.Equal("subtitles", File.ReadAllText(Path.Combine(_scratch, "Dune.srt")));
    }

    [Fact]
    public void The_same_path_and_an_unrelated_file_are_told_apart()
    {
        var one = Path.Combine(_scratch, "one.mkv");
        var two = Path.Combine(_scratch, "two.mkv");
        File.WriteAllText(one, "same bytes");
        File.WriteAllText(two, "same bytes");

        Assert.True(FileIdentity.AreSameFile(one, one));
        Assert.False(FileIdentity.AreSameFile(one, two));
        Assert.False(FileIdentity.AreSameFile(one, Path.Combine(_scratch, "missing.mkv")));
    }

    [Fact]
    public void On_Windows_the_same_file_under_another_letter_case_is_the_same_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Other systems keep different letter case apart unless the drive ignores it.
        }

        var lower = Path.Combine(_scratch, "dune.mkv");
        File.WriteAllText(lower, "dune");

        Assert.True(FileIdentity.AreSameFile(lower, Path.Combine(_scratch, "DUNE.mkv")));
    }

    [Fact]
    public void A_link_to_a_file_is_the_same_file()
    {
        var original = Path.Combine(_scratch, "original.mkv");
        var link = Path.Combine(_scratch, "link.mkv");
        File.WriteAllText(original, "dune");
        try
        {
            File.CreateSymbolicLink(link, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _ = ex;
            return; // Creating a link needs a privilege some machines do not give.
        }

        Assert.True(FileIdentity.AreSameFile(original, link));
    }

    [Fact]
    public void On_Windows_the_same_file_under_a_folder_name_of_another_letter_case_is_the_same_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Other systems keep different letter case apart unless the drive ignores it.
        }

        var folder = Path.Combine(_scratch, "dune (2021)");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Dune (2021).mkv");
        File.WriteAllText(file, "dune");

        Assert.True(FileIdentity.AreSameFile(file, Path.Combine(_scratch, "DUNE (2021)", "Dune (2021).mkv")));
    }

    [Fact]
    public async Task A_letter_case_rename_stopped_between_its_two_steps_is_finished_when_the_job_resumes()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        h.Mover.SameFile = (first, second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        var path = h.Write(Path.Combine("Movies", "Dune (2021)", "dune (2021).mkv"), "the only copy");
        var id = await h.RegisterAsync(path, "Dune", "2021");
        h.Mover.AfterMove = (_, _) => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => h.RunAsync());

        // The file now sits under its temporary name: neither the old nor the new name exists.
        Assert.Single(Directory.EnumerateFiles(Path.Combine(h.SourceDir, "Movies", "Dune (2021)")));
        Assert.EndsWith(CaseSafeRename.TemporarySuffix, Directory.EnumerateFiles(Path.Combine(h.SourceDir, "Movies", "Dune (2021)")).Single(), StringComparison.Ordinal);

        h.Mover.AfterMove = null;
        var result = await h.RunAsync();

        Assert.Equal(0, result.Failed);
        var survivor = Directory.EnumerateFiles(Path.Combine(h.SourceDir, "Movies", "Dune (2021)")).Single();
        Assert.Equal("Dune (2021).mkv", Path.GetFileName(survivor));
        Assert.Equal("the only copy", File.ReadAllText(survivor));
        Assert.Equal(survivor, (await h.Assets.FindByIdAsync(id))!.FilePathRoot);
    }
}
