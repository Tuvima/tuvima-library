using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

public sealed class SourceMutationIntegrationTests
{
    [Fact]
    public void ResolverAndFactory_DenyMutationForExactExistingSource()
    {
        var existingRoot = NewRoot("archive");
        var managedRoot = NewRoot("managed");
        var resolver = CreateResolver(
            Source("archive", existingRoot, LibrarySourceManagementModes.ExistingLibrary, LibrarySourceAccessModes.ReadOnly),
            Source("managed", managedRoot, LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable));

        var resolved = resolver.ResolveSourceForPath(Path.Combine(existingRoot, "Movies", "item.mkv"));

        Assert.NotNull(resolved);
        Assert.Equal("archive", resolved.Source.Id);
        var policy = FileSourceMutationPolicyFactory.Create(resolved.Library, resolved.Source);
        var decision = new SourceMutationPolicyGate().Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.Move,
            Path = Path.Combine(existingRoot, "Movies", "item.mkv"),
        });
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void ResolverAndFactory_AllowManagedWritableParticipatingSource()
    {
        var root = NewRoot("managed");
        var resolver = CreateResolver(
            Source("managed", root, LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable));
        var path = Path.Combine(root, "Movies", "item.mkv");
        var resolved = Assert.IsType<ResolvedLibrarySource>(resolver.ResolveSourceForPath(path));
        var policy = FileSourceMutationPolicyFactory.Create(resolved.Library, resolved.Source);

        Assert.True(new SourceMutationPolicyGate().Evaluate(new SourceMutationRequest
        {
            Source = policy,
            Mutation = SourceMutationKind.Move,
            Path = path,
        }).Allowed);
    }

    [Fact]
    public void Resolver_UsesLongestExactSourceRatherThanSiblingPrefix()
    {
        var root = NewRoot("library");
        var movies = Path.Combine(root, "Movies");
        var moviesArchive = Path.Combine(root, "Movies Archive");
        var resolver = CreateResolver(
            Source("movies", movies, LibrarySourceManagementModes.ManagedByTuvima, LibrarySourceAccessModes.Writable),
            Source("archive", moviesArchive, LibrarySourceManagementModes.ExistingLibrary, LibrarySourceAccessModes.ReadOnly));

        Assert.Equal(
            "archive",
            resolver.ResolveSourceForPath(Path.Combine(moviesArchive, "item.mkv"))?.Source.Id);
        Assert.Null(resolver.ResolveSourceForPath(Path.Combine(root, "Movies2", "item.mkv")));
    }

    [Fact]
    public void FilesystemMutationCallsites_AreGuardedByResolvedSourcePolicy()
    {
        var autoOrganize = ReadRepoSource(@"src/MediaEngine.Ingestion/AutoOrganizeService.cs");
        var writeBack = ReadRepoSource(@"src/MediaEngine.Ingestion/Services/WriteBackService.cs");

        Assert.Contains("if (!CanMoveBetween(asset.FilePathRoot, destPath))", autoOrganize, StringComparison.Ordinal);
        Assert.Contains("if (!CanMoveBetween(asset.FilePathRoot, newDest))", autoOrganize, StringComparison.Ordinal);
        Assert.Contains("CanMutate(bakFile, SourceMutationKind.Delete", autoOrganize, StringComparison.Ordinal);
        Assert.Contains("CanMutate(dir.FullName, SourceMutationKind.Delete", autoOrganize, StringComparison.Ordinal);
        Assert.Contains("ResolveSourceForPath(path)", autoOrganize, StringComparison.Ordinal);
        Assert.Contains("_sourceMutationGate.Evaluate", autoOrganize, StringComparison.Ordinal);

        var resolve = writeBack.IndexOf("ResolveSourceForPath(asset.FilePathRoot)", StringComparison.Ordinal);
        var gate = writeBack.IndexOf("_sourceMutationGate.Evaluate", StringComparison.Ordinal);
        var write = writeBack.IndexOf("tagger.WriteTagsAsync", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && gate > resolve && write > gate);
    }

    private static LibraryFolderResolver CreateResolver(params LibrarySourceEntry[] sources)
    {
        var options = new IngestionOptions
        {
            LibraryFolders =
            [
                new LibraryFolderEntry
                {
                    Id = "library",
                    PreserveOriginals = false,
                    PrimaryDestinationSourceId = sources.FirstOrDefault()?.Id,
                    Sources = sources,
                },
            ],
        };
        return new LibraryFolderResolver(new OptionsMonitorStub<IngestionOptions>(options));
    }

    private static LibrarySourceEntry Source(string id, string path, string managementMode, string accessMode) => new()
    {
        Id = id,
        Path = path,
        ManagementMode = managementMode,
        AccessMode = accessMode,
        ParticipatesInOrganization = true,
    };

    private static string NewRoot(string name)
        => Path.Combine(Path.GetTempPath(), "tuvima-source-integration", Guid.NewGuid().ToString("N"), name);

    private static string ReadRepoSource(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
        return File.ReadAllText(Path.Combine(root, relativePath));
    }
}
