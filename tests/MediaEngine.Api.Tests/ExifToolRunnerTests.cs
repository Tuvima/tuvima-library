using System.Diagnostics;
using MediaEngine.Api.Services.Tools.ExifTool;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>Runs only when <c>TUVIMA_TEST_EXIFTOOL</c> names a real ExifTool script (and Perl is installed); otherwise it is skipped with a clear message.</summary>
public sealed class RealExifToolFactAttribute : FactAttribute
{
    public RealExifToolFactAttribute()
    {
        if (RealExifTool.Find() is null)
        {
            Skip = $"Set {RealExifTool.Variable} to the path of an ExifTool script (for example Image-ExifTool-13.59/exiftool) and install Perl to run this test.";
        }
    }
}

/// <summary>Runs only where a Unix shell can stand in for ExifTool.</summary>
public sealed class ShellStandInFactAttribute : FactAttribute
{
    public ShellStandInFactAttribute()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Skip = "A Unix shell is needed to stand in for ExifTool.";
        }
    }
}

internal static class RealExifTool
{
    public const string Variable = "TUVIMA_TEST_EXIFTOOL";

    public static string? Find()
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }
}

/// <summary>
/// The ExifTool helper program: where it is found, one process for a whole batch, per-file results, and what happens when it
/// stops. The real-ExifTool tests need <c>TUVIMA_TEST_EXIFTOOL</c>; the stand-in tests use a tiny shell script that speaks the
/// same <c>-stay_open</c> protocol.
/// </summary>
public sealed class ExifToolRunnerTests : IDisposable
{
    private const string AddedBy = "XMP-tuvima:AddedBy";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-exiftool-tests", Guid.NewGuid().ToString("N"));

    public ExifToolRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── Real ExifTool ────────────────────────────────────────────────────────

    [RealExifToolFact]
    public async Task AFiftyFileBatch_GoesThroughOneExifToolProcess_ReadsBackEachProfileName_AndLeavesTheMediaDataUntouched()
    {
        var files = MakeBatch(50);
        var runner = RealRunner();
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);
        Assert.True(session.IsRunning);
        Assert.StartsWith("13.", session.Version, StringComparison.Ordinal);

        for (var index = 0; index < files.Count; index++)
        {
            var path = files[index];
            var before = await session.ReadAsync(path, ["ImageDataHash"], CancellationToken.None);
            Assert.False(string.IsNullOrEmpty(before["ImageDataHash"]), $"{path} should have media data to compare.");

            var result = await session.WriteAsync(
                path, new Dictionary<string, string> { [AddedBy] = $"Shy Ünï {index}" }, ExifToolWriteOptions.InPlace(), CancellationToken.None);

            Assert.True(result.IsSuccess, $"{path}: {result.Outcome} {result.Reason}");
            var after = await session.ReadAsync(path, [AddedBy, "ImageDataHash"], CancellationToken.None);
            Assert.Equal($"Shy Ünï {index}", after[AddedBy]);
            Assert.Equal(before["ImageDataHash"], after["ImageDataHash"]);
        }

        Assert.Equal(1, runner.ProcessesStarted);
        Assert.Empty(Directory.GetFiles(_root, "*_original", SearchOption.AllDirectories));
        Assert.Equal(50, files.Count);
    }

    [RealExifToolFact]
    public async Task ACorruptFileInTheBatch_FailsAlone_AUnsupportedOneIsSaidSo_AndTheRestSucceed()
    {
        var good = MakeBatch(4);
        var corrupt = Path.Combine(_root, "corrupt.jpg");
        File.WriteAllText(corrupt, "this is not a picture");
        var text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "plain text cannot hold metadata");
        var runner = RealRunner();
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        var order = new[] { good[0], good[1], corrupt, text, good[2], good[3] };
        var results = new List<ExifToolFileResult>();
        foreach (var path in order)
        {
            results.Add(await session.WriteAsync(
                path, new Dictionary<string, string> { [AddedBy] = "Shy" }, ExifToolWriteOptions.InPlace(), CancellationToken.None));
        }

        Assert.Equal(
            [ExifToolWriteOutcome.Succeeded, ExifToolWriteOutcome.Succeeded, ExifToolWriteOutcome.Failed,
                ExifToolWriteOutcome.Unsupported, ExifToolWriteOutcome.Succeeded, ExifToolWriteOutcome.Succeeded],
            results.Select(result => result.Outcome));
        Assert.False(string.IsNullOrWhiteSpace(results[2].Reason));
        Assert.Contains("TXT", results[3].Reason, StringComparison.Ordinal);
        Assert.True(session.IsRunning);
        Assert.Equal("Shy", (await session.ReadAsync(good[3], [AddedBy], CancellationToken.None))[AddedBy]);
        Assert.Equal(1, runner.ProcessesStarted);
    }

    [RealExifToolFact]
    public async Task WritingToACopy_LeavesTheSourceFileWithoutTheTag()
    {
        var source = MakeBatch(1)[0];
        var copy = Path.Combine(_root, "copy.jpg");
        var runner = RealRunner();
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        var result = await session.WriteAsync(
            source, new Dictionary<string, string> { [AddedBy] = "Shy" }, ExifToolWriteOptions.ToCopy(copy), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Empty(await session.ReadAsync(source, [AddedBy], CancellationToken.None));
        Assert.Equal("Shy", (await session.ReadAsync(copy, [AddedBy], CancellationToken.None))[AddedBy]);
    }

    [RealExifToolFact]
    public async Task WritingToACopy_WithNothingToChange_StillMakesTheCopy_AndSucceeds()
    {
        var source = MakeBatch(1)[0];
        var first = Path.Combine(_root, "first.jpg");
        var second = Path.Combine(_root, "second.jpg");
        var runner = RealRunner();
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        var tagged = await session.WriteAsync(
            source, new Dictionary<string, string> { [AddedBy] = "Shy" }, ExifToolWriteOptions.ToCopy(first), CancellationToken.None);
        var sameAgain = await session.WriteAsync(
            first, new Dictionary<string, string> { [AddedBy] = "Shy" }, ExifToolWriteOptions.ToCopy(second), CancellationToken.None);

        Assert.True(tagged.IsSuccess, tagged.Reason);
        Assert.True(sameAgain.IsSuccess, sameAgain.Reason);
        Assert.True(File.Exists(second));
        Assert.Equal("Shy", (await session.ReadAsync(second, [AddedBy], CancellationToken.None))[AddedBy]);
    }

    [RealExifToolFact]
    public async Task AValueWithALineBreak_ARelativePath_OrABadTagName_IsRefusedAndTheSessionKeepsWorking()
    {
        var path = MakeBatch(1)[0];
        var runner = RealRunner();
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        var lineBreak = await session.WriteAsync(path, new Dictionary<string, string> { [AddedBy] = "two\nlines" }, ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var relative = await session.WriteAsync("picture.jpg", new Dictionary<string, string> { [AddedBy] = "Shy" }, ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var badTag = await session.WriteAsync(path, new Dictionary<string, string> { ["-overwrite_original"] = "x" }, ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var deletingOperator = await session.WriteAsync(path, new Dictionary<string, string> { [AddedBy + "-"] = "Shy" }, ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var noDestination = await session.WriteAsync(path, new Dictionary<string, string> { [AddedBy] = "Shy" }, new ExifToolWriteOptions(), CancellationToken.None);
        var fine = await session.WriteAsync(path, new Dictionary<string, string> { [AddedBy] = "Shy" }, ExifToolWriteOptions.InPlace(), CancellationToken.None);

        Assert.All(new[] { lineBreak, relative, badTag, deletingOperator, noDestination }, result => Assert.Equal(ExifToolWriteOutcome.Failed, result.Outcome));
        Assert.True(fine.IsSuccess, fine.Reason);
    }

    [RealExifToolFact]
    public async Task TheToolsCheck_FindsARealExifTool_AndReportsItsVersion()
    {
        var status = await RealRunner().CheckAsync(CancellationToken.None);

        Assert.Equal(ToolRequirementStates.Ready, status.State);
        Assert.StartsWith("13.", status.Version, StringComparison.Ordinal);
        Assert.False(status.PerlMissing);
    }

    [RealExifToolFact]
    public async Task TheBuiltInConfiguration_DefinesTheTuvimaNamespace_WhenTheConfigFolderHasNoCopy()
    {
        var locator = RealLocator();
        var config = locator.ResolveConfigFile();

        Assert.NotNull(config);
        Assert.StartsWith(Path.Combine(_root, "data"), config, StringComparison.Ordinal);
        var runner = new ExifToolRunner(locator, NullLogger<ExifToolRunner>.Instance);
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);
        var result = await session.WriteAsync(
            MakeBatch(1)[0], new Dictionary<string, string> { [AddedBy] = "Shy" }, ExifToolWriteOptions.InPlace(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Reason);
    }

    // ── A stand-in that speaks the same protocol ─────────────────────────────

    [ShellStandInFact]
    public async Task OneSession_StartsOneProcess_AndEveryCommandGetsItsOwnAnswer()
    {
        var runner = StandInRunner("fine", out var log);
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        var first = await session.WriteAsync(Path.Combine(_root, "a.jpg"), Tags(), ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var second = await session.WriteAsync(Path.Combine(_root, "b.jpg"), Tags(), ExifToolWriteOptions.ToCopy(Path.Combine(_root, "c.jpg")), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, runner.ProcessesStarted);
        var sent = File.ReadAllText(log);
        Assert.Contains("-overwrite_original", sent, StringComparison.Ordinal);
        Assert.Contains("-XMP-tuvima:AddedBy=Shy", sent, StringComparison.Ordinal);
        Assert.Contains("-config", sent, StringComparison.Ordinal);
        Assert.Contains("standin.config -stay_open True -@ -", sent, StringComparison.Ordinal);
        Assert.Contains("-execute2", sent, StringComparison.Ordinal);
    }

    [ShellStandInFact]
    public async Task ExifToolThatExitsPartWay_FailsThatFile_AndLaterCallsSayItStopped_WithoutThrowing()
    {
        var runner = StandInRunner("die", out _);
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        var dying = await session.WriteAsync(Path.Combine(_root, "a.jpg"), Tags(), ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var after = await session.WriteAsync(Path.Combine(_root, "b.jpg"), Tags(), ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var read = await session.ReadAsync(Path.Combine(_root, "b.jpg"), [AddedBy], CancellationToken.None);

        Assert.Equal(ExifToolWriteOutcome.Failed, dying.Outcome);
        Assert.Equal(ExifToolFileResult.Failed("ExifTool stopped"), after);
        Assert.Empty(read);
        Assert.False(session.IsRunning);
    }

    [ShellStandInFact]
    public async Task ExifToolThatStopsAnswering_TimesOutThatFile_AndIsNotUsedAgain()
    {
        var runner = StandInRunner("hang", out _, commandTimeout: TimeSpan.FromSeconds(1));
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        var watch = Stopwatch.StartNew();
        var hung = await session.WriteAsync(Path.Combine(_root, "a.jpg"), Tags(), ExifToolWriteOptions.InPlace(), CancellationToken.None);
        var after = await session.WriteAsync(Path.Combine(_root, "b.jpg"), Tags(), ExifToolWriteOptions.InPlace(), CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "The timeout should end the wait, not the stand-in's own sleep.");
        Assert.Equal(ExifToolWriteOutcome.Failed, hung.Outcome);
        Assert.Contains("did not answer", hung.Reason, StringComparison.Ordinal);
        Assert.Equal(ExifToolFileResult.Failed("ExifTool stopped"), after);
        Assert.False(session.IsRunning);
    }

    [ShellStandInFact]
    public async Task ACancelledWrite_StopsExifTool_AndThrowsTheCancellation()
    {
        var runner = StandInRunner("hang", out _, commandTimeout: TimeSpan.FromSeconds(30));
        await using var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.WriteAsync(Path.Combine(_root, "a.jpg"), Tags(), ExifToolWriteOptions.InPlace(), cancel.Token));

        Assert.False(session.IsRunning);
    }

    [ShellStandInFact]
    public async Task DisposingASession_TellsExifToolToFinish()
    {
        var runner = StandInRunner("fine", out var log);
        var session = await runner.OpenSessionAsync(CancellationToken.None);
        Assert.NotNull(session);

        await session.DisposeAsync();

        Assert.False(session.IsRunning);
        var sent = File.ReadAllLines(log);
        Assert.Contains("False", sent);
        Assert.Contains("-stay_open", sent);
    }

    [ShellStandInFact]
    public async Task ExifToolThatCannotAnswerItsFirstQuestion_GivesNoSession()
    {
        var script = Path.Combine(_root, "silent.sh");
        File.WriteAllText(script, "#!/bin/sh\nexit 1\n");
        var runner = new ExifToolRunner(new StubLocator(new ExifToolCommand("sh", [script])), NullLogger<ExifToolRunner>.Instance);

        var session = await runner.OpenSessionAsync(CancellationToken.None);

        Assert.Null(session);
    }

    // ── Where ExifTool is found ──────────────────────────────────────────────

    [Fact]
    public async Task WithNoExifToolAnywhere_TheCheckSaysMissing_AndNoSessionOpens()
    {
        var runner = new ExifToolRunner(new StubLocator(null), NullLogger<ExifToolRunner>.Instance);

        var status = await runner.CheckAsync(CancellationToken.None);
        var session = await runner.OpenSessionAsync(CancellationToken.None);

        Assert.Equal(ToolRequirementStates.Missing, status.State);
        Assert.Equal(!OperatingSystem.IsWindows(), status.PerlMissing);
        Assert.Null(session);
        Assert.Equal(0, runner.ProcessesStarted);
    }

    [Fact]
    public async Task APerlPresentButNoExifTool_IsMissingWithoutBlamingPerl()
    {
        var runner = new ExifToolRunner(new StubLocator(null, perl: "/usr/bin/perl"), NullLogger<ExifToolRunner>.Instance);

        var status = await runner.CheckAsync(CancellationToken.None);

        Assert.Equal(ToolRequirementStates.Missing, status.State);
        Assert.False(status.PerlMissing);
    }

    [Fact]
    public void TheDownloadedCopy_WinsOverTheSettingAndTheSearchPath_AndTheSettingWinsOverTheSearchPath()
    {
        var bin = Path.Combine(_root, "bin");
        var setting = Path.Combine(_root, "setting", "exiftool");
        var downloaded = Path.Combine(_root, "data", "tools", "exiftool", "exiftool");
        Touch(Path.Combine(bin, PerlName));
        Touch(Path.Combine(bin, OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool"));
        Touch(setting);

        var locator = Locator(bin, setting);
        Assert.Equal([setting], locator.Resolve()!.LeadingArguments);

        Touch(downloaded);
        Assert.Equal([downloaded], locator.Resolve()!.LeadingArguments);
        Assert.Equal(Path.Combine(bin, PerlName), locator.Resolve()!.FileName);

        File.Delete(downloaded);
        File.Delete(setting);
        Assert.Equal(Path.Combine(bin, OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool"), locator.Resolve()!.FileName);
    }

    [Fact]
    public void AWindowsBuildFolder_RunsItsOwnPerlOnItsOwnScript()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Combine(_root, "data", "tools", "exiftool", "exiftool_files");
        Touch(Path.Combine(folder, "perl.exe"));
        Touch(Path.Combine(folder, "exiftool.pl"));

        var command = Locator(null, null).Resolve();

        Assert.Equal(Path.Combine(folder, "perl.exe"), command!.FileName);
        Assert.Equal([Path.Combine(folder, "exiftool.pl")], command.LeadingArguments);
    }

    [Fact]
    public void AScriptWithNoPerlToRunIt_IsNotUsed_ExceptWhenItIsExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var script = Path.Combine(_root, "setting", "exiftool");
        Touch(script);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var locator = Locator(null, script);
        Assert.Null(locator.Resolve());

        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var command = locator.Resolve();
        Assert.Equal(script, command!.FileName);
        Assert.Empty(command.LeadingArguments);
    }

    [Fact]
    public void TheConfigFoldersCopyOfTheConfiguration_IsUsedWhenItIsThere_OtherwiseTheBuiltInOneIsWrittenToTheDataFolder()
    {
        var locator = Locator(null, null);

        var builtIn = locator.ResolveConfigFile();

        Assert.NotNull(builtIn);
        Assert.StartsWith(Path.Combine(_root, "data"), builtIn, StringComparison.Ordinal);
        var text = File.ReadAllText(builtIn);
        Assert.Contains("https://tuvima.github.io/tuvima-library/ns/xmp/1.0/", text, StringComparison.Ordinal);
        Assert.Contains("AddedBy", text, StringComparison.Ordinal);

        var shipped = Path.Combine(_root, "config", "exiftool", ExifToolLocator.ConfigFileName);
        Touch(shipped);
        Assert.Equal(shipped, locator.ResolveConfigFile());
    }

    [Fact]
    public void TheConfigurationFileInTheRepository_IsTheSameOneThatIsBuiltIn()
    {
        var shipped = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "config", "exiftool", ExifToolLocator.ConfigFileName));
        var builtIn = File.ReadAllText(Locator(null, null).ResolveConfigFile()!);

        Assert.Equal(shipped, builtIn);
        Assert.Contains("'tuvima' => 'https://tuvima.github.io/tuvima-library/ns/xmp/1.0/'", shipped, StringComparison.Ordinal);
        Assert.Contains("AddedBy", shipped, StringComparison.Ordinal);
    }

    // ── Credits ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheReadme_TheAttributionsPage_AndTheThirdPartyNotices_AllCreditExifTool()
    {
        var root = FindRepositoryRoot();

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var attributions = File.ReadAllText(Path.Combine(root, "docs", "reference", "attributions.md"));
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));

        Assert.Contains("[ExifTool](https://exiftool.org/) by Phil Harvey", readme, StringComparison.Ordinal);
        Assert.Contains("| ExifTool |", attributions, StringComparison.Ordinal);
        Assert.Contains("Perl Artistic License or GNU General Public License", attributions, StringComparison.Ordinal);
        Assert.Contains("Strawberry Perl", attributions, StringComparison.Ordinal);
        Assert.Contains("Copyright 2003-2026, Phil Harvey", notices, StringComparison.Ordinal);
        Assert.Contains("44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec", notices, StringComparison.Ordinal);
        Assert.Contains("668ea3acececb7235fbd0f4900e72d5f12c9b07e5c778fd36cb1e9b5828fd65a", notices, StringComparison.Ordinal);
        Assert.Contains("Licenses_Strawberry_Perl.zip", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedDownloadSettings_AndTheNotices_NameTheSameChecksums()
    {
        var root = FindRepositoryRoot();
        var downloads = File.ReadAllText(Path.Combine(root, "config", "tool_downloads.json")).ToLowerInvariant();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md")).ToLowerInvariant();

        foreach (var checksum in new[]
        {
            "44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec",
            "4d61ebe19311dbf7b9710ac2c6c402e3cba3e23b63e8b82be88e471343bed52d",
            "9e8e817fab9fdc2e37f7cd023aa34e76d1ef561834dfd791a48a7e327cc8f4b1",
            "668ea3acececb7235fbd0f4900e72d5f12c9b07e5c778fd36cb1e9b5828fd65a",
            "a6b8018093845ac93011307bac4ab630d6604b2365fc7f66bb88c88d7dedb5ed",
        })
        {
            Assert.Contains(checksum, downloads, StringComparison.Ordinal);
            Assert.Contains(checksum, notices, StringComparison.Ordinal);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string PerlName => OperatingSystem.IsWindows() ? "perl.exe" : "perl";

    private static IReadOnlyDictionary<string, string> Tags() => new Dictionary<string, string> { [AddedBy] = "Shy" };

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "placeholder");
    }

    /// <summary>A locator over this test's own data and config folders, with the given search path and ExifTool setting.</summary>
    private ExifToolLocator Locator(string? searchPath, string? exifToolSetting)
    {
        var paths = new TuvimaDataPaths(Path.Combine(_root, "data"));
        if (!string.Equals(paths.Root, Path.GetFullPath(Path.Combine(_root, "data")), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TUVIMA_DATA_DIR is set, so this test would write outside its own folder.");
        }

        return new ExifToolLocator(
            paths,
            new ConfigurationDirectoryLoader(Path.Combine(_root, "config")),
            NullLogger<ExifToolLocator>.Instance,
            name => name switch
            {
                ExifToolLocator.PathVariable => exifToolSetting,
                "PATH" => searchPath ?? string.Empty,
                _ => null,
            });
    }

    private ExifToolLocator RealLocator() =>
        // The real search path, so the real Perl is found; the real ExifTool is named by the test setting.
        new(new TuvimaDataPaths(Path.Combine(_root, "data")),
            new ConfigurationDirectoryLoader(Path.Combine(_root, "config")),
            NullLogger<ExifToolLocator>.Instance,
            name => name == ExifToolLocator.PathVariable ? RealExifTool.Find() : Environment.GetEnvironmentVariable(name));

    private ExifToolRunner RealRunner() => new(RealLocator(), NullLogger<ExifToolRunner>.Instance);

    private ExifToolRunner StandInRunner(string mode, out string log, TimeSpan? commandTimeout = null)
    {
        var script = Path.Combine(_root, "standin.sh");
        log = Path.Combine(_root, "standin-" + mode + ".log");
        File.WriteAllText(script, """
            #!/bin/sh
            # Speaks ExifTool's -stay_open protocol: answers each -execute<N>, then {ready<N>} on stdout and {done<N>} on stderr.
            mode="$1"
            log="$2"
            count=0
            printf 'args: %s\n' "$*" >> "$log"
            while IFS= read -r line; do
              printf '%s\n' "$line" >> "$log"
              case "$line" in
                -execute*)
                  id="${line#-execute}"
                  count=$((count+1))
                  if [ "$count" -ge 2 ] && [ "$mode" = "die" ]; then exit 3; fi
                  if [ "$count" -ge 2 ] && [ "$mode" = "hang" ]; then sleep 60; fi
                  if [ "$count" -eq 1 ]; then echo "13.59"; else echo "    1 image files updated"; fi
                  echo "{ready$id}"
                  echo "{done$id}" >&2
                  ;;
                False) exit 0 ;;
              esac
            done
            """.Replace("\r\n", "\n") + "\n");
        var config = Path.Combine(_root, "standin.config");
        File.WriteAllText(config, "1;\n");
        return new ExifToolRunner(
            new StubLocator(new ExifToolCommand("sh", [script, mode, log]), config: config),
            NullLogger<ExifToolRunner>.Instance,
            commandTimeout);
    }

    /// <summary>Writes <paramref name="count"/> small pictures and clips (JPEG, PNG and, when FFmpeg is installed, MP4) with accented names.</summary>
    private List<string> MakeBatch(int count)
    {
        var folder = Path.Combine(_root, "batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var video = FindFfmpeg() is { } ffmpeg ? MakeVideo(ffmpeg, folder) : null;
        var files = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var kind = video is not null && index % 10 >= 7 ? "mp4" : index % 2 == 0 ? "jpg" : "png";
            var path = Path.Combine(folder, $"sample {index:00} café.{kind}");
            switch (kind)
            {
                case "mp4":
                    File.Copy(video!, path);
                    break;
                default:
                    WriteImage(path, kind == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, index);
                    break;
            }

            files.Add(path);
        }

        return files;
    }

    private static void WriteImage(string path, SKEncodedImageFormat format, int seed)
    {
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(new SKColor((byte)(seed * 5 % 256), (byte)(255 - seed * 3 % 256), (byte)(seed * 7 % 256)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static string? MakeVideo(string ffmpeg, string folder)
    {
        var path = Path.Combine(folder, "base.mp4");
        using var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64:d=1", "-pix_fmt", "yuv420p", path },
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        process!.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && File.Exists(path) ? path : null;
    }

    private static string? FindFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), name))
            .FirstOrDefault(File.Exists);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config", "tool_downloads.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("config/tool_downloads.json was not found above the test folder.");
    }

    private sealed class StubLocator(ExifToolCommand? command, string? perl = null, string? config = null) : IExifToolLocator
    {
        public ExifToolCommand? Resolve() => command;

        public string? FindPerl() => perl;

        public string? ResolveConfigFile() => config;
    }
}
