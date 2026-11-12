using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The move that may not take another name (a video following its series into a folder): a taken name is refused and left alone, and a move that
/// stops part way through a copy leaves nothing behind that it created and is not mistaken for a taken name.
/// </summary>
public sealed class FileOrganizerNoRenameMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-organizer-norename-{Guid.NewGuid():N}");

    public FileOrganizerNoRenameMoveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ATakenName_IsRefused_AndTheFileThatWasThereIsNeverTouched()
    {
        var source = Write("a/Clip.mp4", "mine");
        var taken = Write("b/Clip.mp4", "theirs");
        var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);

        var result = await organizer.ExecuteMoveWithoutRenameAsync(source, taken);

        Assert.Null(result);
        Assert.Equal("theirs", File.ReadAllText(taken));
        Assert.Equal("mine", File.ReadAllText(source));
    }

    [Fact]
    public async Task AFreeName_IsMoved_WithNothingLeftOverInTheDestinationFolder()
    {
        var source = Write("a/Clip.mp4", "mine");
        var destination = Path.Combine(_root, "b", "Clip.mp4");
        var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);

        var result = await organizer.ExecuteMoveWithoutRenameAsync(source, destination);

        Assert.Equal(destination, result);
        Assert.Equal("mine", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
        Assert.Equal(["Clip.mp4"], Directory.GetFiles(Path.Combine(_root, "b")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AMoveThatStopsPartWayThroughACopy_RemovesThePartialFileItMade_AndLeavesTheSourceWhole()
    {
        var source = Write("a/Clip.mp4", "the whole video");
        var destination = Path.Combine(_root, "b", "Clip.mp4");
        var organizer = new FileOrganizer(
            NullLogger<FileOrganizer>.Instance,
            moveFile: (from, to, overwrite) =>
            {
                // What a copy between two drives does when the disk fills up: some bytes are in place, then the move fails.
                File.WriteAllText(to, "the who");
                throw new IOException("There is not enough space on the disk.");
            },
            retryDelay: TimeSpan.FromMilliseconds(1));

        var result = await organizer.ExecuteMoveWithoutRenameAsync(source, destination);

        Assert.Null(result);
        Assert.False(File.Exists(destination), "the partial copy is removed, so the failure is not read as a taken name");
        Assert.Equal("the whole video", File.ReadAllText(source));
    }

    [Fact]
    public async Task AFailedMove_NeverRemovesAFileThatWasAlreadyAtTheDestination()
    {
        var source = Write("a/Clip.mp4", "mine");
        var taken = Write("b/Clip.mp4", "theirs");
        var organizer = new FileOrganizer(
            NullLogger<FileOrganizer>.Instance,
            moveFile: (from, to, overwrite) => throw new IOException("in use"),
            retryDelay: TimeSpan.FromMilliseconds(1));

        var result = await organizer.ExecuteMoveWithoutRenameAsync(source, taken);

        Assert.Null(result);
        Assert.Equal("theirs", File.ReadAllText(taken));
    }

    [Fact]
    public async Task AMoveThatFinishes_KeepsTheMovedFile_EvenIfSomethingAfterTheMoveFails()
    {
        var source = Write("a/Clip.mp4", "mine");
        var destination = Path.Combine(_root, "b", "Clip.mp4");
        var organizer = new FileOrganizer(
            NullLogger<FileOrganizer>.Instance,
            new ThrowingHashCache(),
            retryDelay: TimeSpan.FromMilliseconds(1));

        var result = await organizer.ExecuteMoveWithoutRenameAsync(source, destination);

        Assert.Null(result);
        Assert.Equal("mine", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }

    /// <summary>A hash cache whose update fails after the file has already moved.</summary>
    private sealed class ThrowingHashCache : IFileHashCacheRepository
    {
        public Task<string?> TryGetAsync(string absolutePath, long sizeBytes, DateTimeOffset mtimeUtc, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task UpsertAsync(string absolutePath, long sizeBytes, DateTimeOffset mtimeUtc, string sha256, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string absolutePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task MoveAsync(string sourceAbsolutePath, string destinationAbsolutePath, CancellationToken ct = default) =>
            throw new InvalidOperationException("The cache could not be updated.");
    }
}
