using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A shortcut (symbolic link or Windows junction) inside a Managed folder may lead to an As-is folder or another drive. The
/// conversion never lists, previews, moves, renames or deletes anything reached through one, and a shortcut that points back up
/// the tree cannot make the walk loop.
/// </summary>
public sealed class FolderConversionLinkTests : IDisposable
{
    private readonly List<FolderConversionHarness> _harnesses = [];
    private readonly List<string> _links = [];

    public void Dispose()
    {
        // Links are removed first so the clean-up of the temp folder can never reach what they point at.
        foreach (var link in _links)
        {
            RemoveLink(link);
        }

        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }
    }

    private FolderConversionHarness NewHarness(bool managed = true)
    {
        var harness = new FolderConversionHarness(managed: managed);
        _harnesses.Add(harness);
        return harness;
    }

    [Fact]
    public async Task A_symbolic_link_to_an_outside_folder_is_not_listed_previewed_moved_or_removed()
    {
        var h = NewHarness();
        var (outsideFile, link) = await SetUpOutsideAsync(h, junction: false);
        if (link is null)
        {
            return; // This machine does not allow creating a symbolic link, so there is nothing to prove here.
        }

        await AssertLeftAloneAsync(h, outsideFile);
    }

    [Fact]
    public async Task A_junction_to_an_outside_folder_is_not_listed_previewed_moved_or_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Junctions exist only on Windows.
        }

        var h = NewHarness();
        var (outsideFile, link) = await SetUpOutsideAsync(h, junction: true);
        if (link is null)
        {
            return;
        }

        await AssertLeftAloneAsync(h, outsideFile);
    }

    [Fact]
    public async Task A_symbolic_link_to_a_single_file_is_not_moved()
    {
        var h = NewHarness();
        var real = Path.Combine(h.Root, "elsewhere", "Heat.1995.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllText(real, "heat");
        var link = Path.Combine(h.SourceDir, "Heat.1995.mkv");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _ = ex;
            return; // This machine does not allow creating a symbolic link.
        }

        _links.Add(link);
        await h.RegisterAsync(real, "Heat", "1995");

        var result = await h.RunAsync();
        var preview = await h.Previewer.PreviewAsync(h.SourceId, fileLimit: 0);

        Assert.Equal(0, result.Total);
        Assert.True(File.Exists(real));
        Assert.True(File.Exists(link));
        Assert.Empty(h.Mover.Moves);
        Assert.NotNull(preview);
        Assert.Equal(0, preview.TotalFiles);
        Assert.Equal(1, preview.SkippedLinks);
    }

    [Fact]
    public void A_link_that_points_back_up_the_tree_does_not_make_the_walk_loop()
    {
        var h = NewHarness();
        var real = h.Write(Path.Combine("a", "Dune.2021.mkv"));
        var loop = Path.Combine(h.SourceDir, "a", "again");
        if (!TryCreateDirectoryLink(loop, h.SourceDir, junction: OperatingSystem.IsWindows()))
        {
            return; // This machine does not allow creating a link.
        }

        _links.Add(loop);

        var (files, skipped) = FolderMediaEnumerator.EnumerateCountingLinks(
            h.SourceDir, includeSubdirectories: true, FolderMediaEnumerator.DefaultMediaExtensions, skipLinks: true);

        Assert.Equal([real], files);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public async Task A_catalogued_file_reached_through_a_link_is_never_moved_even_if_it_is_asked_for_directly()
    {
        var h = NewHarness();
        var (outsideFile, link) = await SetUpOutsideAsync(h, junction: OperatingSystem.IsWindows(), registerThroughLink: true);
        if (link is null)
        {
            return;
        }

        var context = FolderConversionContext.TryCreate(h.Config.LoadLibraries(), h.OperationId, h.SourceId, out _);
        Assert.NotNull(context);
        var file = Assert.Single(await h.Assets.ListFilesUnderAsync(h.SourceDir));
        Assert.Equal(Path.Combine(link, Path.GetFileName(outsideFile)), file.FilePath);

        var decision = await h.Organiser.DecideAsync(file, context, plannedTargets: null);

        Assert.Equal(OrganiseDecisionKind.Denied, decision.Kind);
        Assert.Contains("shortcut", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public void A_folder_that_is_itself_a_shortcut_is_not_treated_as_a_shortcut_inside_it()
    {
        var h = NewHarness();
        var real = Path.Combine(h.Root, "real");
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        var file = Path.Combine(real, "sub", "Dune.2021.mkv");
        File.WriteAllText(file, "dune");
        var rootLink = Path.Combine(h.Root, "RootLink");
        if (!TryCreateDirectoryLink(rootLink, real, junction: OperatingSystem.IsWindows()))
        {
            return; // This machine does not allow creating a link.
        }

        _links.Add(rootLink);
        var throughRoot = Path.Combine(rootLink, "sub", "Dune.2021.mkv");

        // The chosen folder being a shortcut is fine for conversion; the merge still refuses it.
        Assert.False(FolderMediaEnumerator.IsReachedThroughLink(throughRoot, rootLink, includeRoot: false));
        Assert.True(FolderMediaEnumerator.IsReachedThroughLink(throughRoot, rootLink));
        Assert.Equal([Path.Combine(rootLink, "sub", "Dune.2021.mkv")], FolderMediaEnumerator.Enumerate(
            rootLink, includeSubdirectories: true, FolderMediaEnumerator.DefaultMediaExtensions, skipLinks: true));
    }

    private async Task AssertLeftAloneAsync(FolderConversionHarness h, string outsideFile)
    {
        var inside = h.Write("Dune.2021.mkv", "dune");
        var insideId = await h.RegisterAsync(inside, "Dune", "2021");

        var quick = await h.Previewer.CountAsync(h.SourceId);
        var preview = await h.Previewer.PreviewAsync(h.SourceId, fileLimit: 0);
        var result = await h.RunAsync();

        Assert.Equal((1, 1), quick);
        Assert.NotNull(preview);
        Assert.Equal(1, preview.TotalFiles);
        Assert.Equal(1, preview.SkippedLinks);
        Assert.Equal(1, result.Total);

        // Only the real file inside the folder was renamed; the outside file is exactly where and what it was.
        Assert.True(File.Exists(outsideFile));
        Assert.Equal("heat", File.ReadAllText(outsideFile));
        Assert.Equal(
            Path.Combine(h.SourceDir, "Movies", "Dune (2021)", "Dune (2021).mkv"),
            (await h.Assets.FindByIdAsync(insideId))!.FilePathRoot);
        Assert.DoesNotContain(h.Mover.Moves, move => move.ToString()!.Contains("Heat", StringComparison.Ordinal));
    }

    private async Task<(string OutsideFile, string? Link)> SetUpOutsideAsync(FolderConversionHarness h, bool junction, bool registerThroughLink = false)
    {
        var outside = Path.Combine(h.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "Heat.1995.mkv");
        File.WriteAllText(outsideFile, "heat");
        var link = Path.Combine(h.SourceDir, "Link");
        if (!TryCreateDirectoryLink(link, outside, junction))
        {
            return (outsideFile, null);
        }

        _links.Add(link);
        await h.RegisterAsync(registerThroughLink ? Path.Combine(link, Path.GetFileName(outsideFile)) : outsideFile, "Heat", "1995");
        return (outsideFile, link);
    }

    private static bool TryCreateDirectoryLink(string link, string target, bool junction)
    {
        try
        {
            if (junction)
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                });
                process!.WaitForExit();
                return Directory.Exists(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _ = ex;
            return false;
        }
    }

    private static void RemoveLink(string link)
    {
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            else if (File.Exists(link))
            {
                File.Delete(link);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Test clean-up only; the temp folder is removed with everything in it.
            _ = ex;
        }
    }
}
