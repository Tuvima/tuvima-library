using System.Text.Json;
using System.Xml.Linq;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Services;

namespace MediaEngine.AI.Tests;

public sealed class SharedAiRuntimeTests
{
    [Fact]
    public void RequiredNativeVersionsMatchPinnedManagedPackages()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var versions = XDocument.Load(Path.Combine(directory.FullName, "Directory.Packages.props"));
        string? Version(string id) => versions.Descendants("PackageVersion").Single(x => (string?)x.Attribute("Include") == id).Attribute("Version")?.Value;
        Assert.Equal(SharedAiRuntime.LlamaVersion, Version("LLamaSharp"));
        Assert.Equal(SharedAiRuntime.WhisperVersion, Version("Whisper.net"));
    }

    [Fact]
    public void ManifestRejectsCorruptionWrongPlatformAndTraversal()
    {
        using var directory = new Scratch();
        var file = Path.Combine(directory.Path, "native.dll");
        File.WriteAllText(file, "verified-test-payload");
        using var stream = File.OpenRead(file);
        var hash = Hashing.Sha256Hex(stream);
        stream.Dispose();
        void Manifest(string path = "native.dll", string rid = "win-x64") => File.WriteAllText(Path.Combine(directory.Path, "manifest.json"), JsonSerializer.Serialize(new
        {
            version = "1.0",
            rid,
            files = new[] { new { path, size = new FileInfo(file).Length, sha256 = hash } }
        }));
        Manifest();
        SharedAiRuntime.VerifyManifest(directory.Path, "1.0", "win-x64");
        Assert.Throws<InvalidDataException>(() => SharedAiRuntime.VerifyManifest(directory.Path, "1.0", "linux-x64"));
        Manifest("../outside.dll");
        Assert.Throws<InvalidOperationException>(() => SharedAiRuntime.VerifyManifest(directory.Path, "1.0", "win-x64"));
        Manifest();
        File.WriteAllText(file, "tampered-test-payload");
        Assert.Throws<InvalidDataException>(() => SharedAiRuntime.VerifyManifest(directory.Path, "1.0", "win-x64"));
    }

    [Fact]
    public void MissingInstallationDoesNotCreateDirectoriesOrSearchBuildOutputs()
    {
        using var directory = new Scratch();
        var missing = Path.Combine(directory.Path, "not-installed");
        Assert.Throws<FileNotFoundException>(() => SharedAiRuntime.VerifyManifest(missing, "1.0", "win-x64"));
        Assert.False(Directory.Exists(missing));
        var errors = AiSettingsValidator.Validate(new AiSettings { NativeRuntimeDirectory = "relative/runtimes" });
        Assert.Contains(errors, error => error.Path == "native_runtime_directory");
    }

    [Fact]
    public void SharedReadersBlockMutationsAndUnmanagedModelsCannotBeDeleted()
    {
        using var directory = new Scratch();
        var artifact = Path.Combine(directory.Path, "llama", "model.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "model");
        using (SharedModelArtifact.AcquireRead(artifact))
        {
            using (SharedModelArtifact.AcquireRead(artifact))
            {
                Assert.Throws<IOException>(() => SharedModelArtifact.AcquireWrite(artifact));
            }
        }
        using (SharedModelArtifact.AcquireWrite(artifact))
        {
            Assert.Throws<IOException>(() => SharedModelArtifact.AcquireRead(artifact));
            Assert.Throws<InvalidOperationException>(() => SharedModelArtifact.RequireManaged(artifact));
            SharedModelArtifact.RecordOwnership(artifact);
            SharedModelArtifact.RequireManaged(artifact);
            File.WriteAllText(artifact, "changed outside Tuvima");
            Assert.Throws<InvalidOperationException>(() => SharedModelArtifact.RequireManaged(artifact));
        }
    }

    [Fact]
    public void StagingFilesAreUniqueAndRemainOnTheModelVolume()
    {
        using var directory = new Scratch();
        var artifact = Path.Combine(directory.Path, "llama", "model.gguf");
        var first = SharedModelArtifact.StagingPath(artifact);
        var second = SharedModelArtifact.StagingPath(artifact);
        Assert.NotEqual(first, second);
        Assert.StartsWith(Path.Combine(directory.Path, ".tuvima", "staging"), first);
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tuvima-shared-ai-" + Guid.NewGuid().ToString("N"));
        public Scratch() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
