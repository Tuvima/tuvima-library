using MediaEngine.Api.Services.FileWrites;

namespace MediaEngine.Api.Tests;

/// <summary>What counts as "in use" before Library files a file on its own: a lock or sharing conflict, and nothing else.</summary>
public sealed class FileInUseProbeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"tuvima_probe_{Guid.NewGuid():N}");

    public FileInUseProbeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder that is still locked is left for the operating system's temp clean-up.
        }
    }

    [Fact]
    public void AFileAnotherHandleHoldsExclusively_IsInUse_AndIsLeftUnchangedByTheProbe()
    {
        var path = Write("a.flac", "bytes");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(FileInUseProbe.IsInUse(path));
        }

        Assert.False(FileInUseProbe.IsInUse(path));
        Assert.Equal("bytes", File.ReadAllText(path));
    }

    [Fact]
    public void AFileThatIsNotThere_OrAFolder_IsNotInUse()
    {
        Assert.False(FileInUseProbe.IsInUse(Path.Combine(_folder, "missing.flac")));
        Assert.False(FileInUseProbe.IsInUse(_folder));
        Assert.False(FileInUseProbe.IsInUse(null));
    }

    [Fact]
    public void OnlyASharingOrLockViolationCountsAsInUse_AnyOtherDiskErrorDoesNot()
    {
        var conflict = OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : 11;
        var lockViolation = OperatingSystem.IsWindows() ? unchecked((int)0x80070021) : 35;

        Assert.True(FileInUseProbe.IsLockConflict(new IOException("shared", conflict)));
        Assert.True(FileInUseProbe.IsLockConflict(new IOException("locked", lockViolation)));
        Assert.False(FileInUseProbe.IsLockConflict(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.False(FileInUseProbe.IsLockConflict(new IOException("some other error", 5)));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }
}
