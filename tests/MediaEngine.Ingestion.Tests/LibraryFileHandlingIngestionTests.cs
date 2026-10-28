using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A library's own duplicate rule and a folder's scan switch and overrides reach the code that acts on files: the duplicate
/// rule that applies, the folders that are scanned and watched, the folder a path belongs to, and what the gate lets change.
/// </summary>
public sealed class LibraryFileHandlingIngestionTests : IDisposable
{
    private readonly ImportFolderHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void A_library_duplicate_rule_overrides_the_server_rule_for_an_import_copy_of_a_file_it_holds()
    {
        var movies = _h.Config.Libraries.Libraries[0];
        var held = Path.Combine(_h.MoviesDir, "Dune.2021.mkv");

        Assert.Equal(LibraryDuplicatePolicies.KeepOne, _h.Importer.ResolveDuplicatePolicy(held));

        movies.FileHandling.DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth;

        Assert.Equal(LibraryDuplicatePolicies.KeepOne, _h.Importer.DuplicatePolicy);
        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, _h.Importer.ResolveDuplicatePolicy(held));
    }

    [Fact]
    public void A_folder_duplicate_rule_overrides_its_library_for_an_import_copy_of_a_file_it_holds()
    {
        var movies = _h.Config.Libraries.Libraries[0];
        movies.FileHandling.DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth;
        movies.Sources.First(source => source.Id == "movies-new").FileHandlingOverride =
            new LibrarySourceFileHandlingOverride { DuplicatePolicy = LibraryDuplicatePolicies.KeepOne };

        Assert.Equal(LibraryDuplicatePolicies.KeepOne, _h.Importer.ResolveDuplicatePolicy(Path.Combine(_h.MoviesDir, "Dune.2021.mkv")));
        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, _h.Importer.ResolveDuplicatePolicy(Path.Combine(_h.ElsewhereDir, "Dune.2021.mkv")));
    }

    [Fact]
    public void A_file_held_outside_every_library_follows_the_server_duplicate_rule()
    {
        _h.Config.Libraries.Libraries[0].FileHandling.DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth;

        Assert.Equal(LibraryDuplicatePolicies.KeepOne, _h.Importer.ResolveDuplicatePolicy(Path.Combine(_h.AsIsDir, "x.mkv")));
    }

    [Fact]
    public void The_runtime_library_entry_carries_the_library_rule_and_each_folder_override()
    {
        var library = _h.Config.Libraries.Libraries[0];
        library.FileHandling.DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth;
        library.Sources[0].ScanEnabled = false;
        library.Sources[0].FileHandlingOverride = new LibrarySourceFileHandlingOverride
        {
            DuplicatePolicy = LibraryDuplicatePolicies.KeepOne,
            WriteTags = false,
        };

        var entry = LibraryFolderEntryFactory.Create(library);

        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, entry.ResolveDuplicatePolicy(null, LibraryDuplicatePolicies.KeepOne));
        Assert.Equal(LibraryDuplicatePolicies.KeepOne, entry.ResolveDuplicatePolicy(entry.Sources[0], LibraryDuplicatePolicies.KeepBoth));
        Assert.False(entry.Sources[0].ScanEnabled);
        Assert.False(entry.Sources[0].WriteTagsOverride);
        Assert.True(entry.Sources[1].ScanEnabled);
    }

    [Fact]
    public void A_folder_with_scanning_off_is_not_scanned_watched_or_resolved_but_still_counts_for_overlap_checks()
    {
        var library = _h.Config.Libraries.Libraries[0];
        library.Sources.First(source => source.Id == "movies-other").ScanEnabled = false;
        var entry = LibraryFolderEntryFactory.Create(library);
        _h.Options.LibraryFolders = [entry];
        var resolver = new LibraryFolderResolver(new OptionsMonitorStub<IngestionOptions>(_h.Options));

        Assert.DoesNotContain(_h.ElsewhereDir, entry.EffectiveSourcePaths);
        Assert.Contains(_h.MoviesDir, entry.EffectiveSourcePaths);
        Assert.Contains(_h.ElsewhereDir, entry.CataloguedSourcePaths);
        Assert.Null(resolver.ResolveSourceForPath(Path.Combine(_h.ElsewhereDir, "Dune.2021.mkv")));
        Assert.NotNull(resolver.ResolveSourceForPath(Path.Combine(_h.MoviesDir, "Dune.2021.mkv")));
    }

    [Fact]
    public void A_folder_with_scanning_off_refuses_every_file_change_through_the_gate()
    {
        var root = Path.Combine(Path.GetTempPath(), "tuvima-scan-off", Guid.NewGuid().ToString("N"));
        var source = new LibrarySourceConfig
        {
            Id = "source",
            Path = root,
            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
            AccessMode = LibrarySourceAccessModes.Writable,
            ScanEnabled = false,
        };
        var library = new LibraryFolderConfig { Id = "library", Sources = [source], PrimaryDestinationSourceId = "source" };
        var gate = new SourceMutationPolicyGate();
        var policy = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true);

        Assert.False(policy.ScanEnabled);
        foreach (var mutation in Enum.GetValues<SourceMutationKind>())
        {
            var decision = gate.Evaluate(new SourceMutationRequest { Source = policy, Mutation = mutation, Path = Path.Combine(root, "a.txt") });
            Assert.False(decision.Allowed, mutation.ToString());
            Assert.Contains("paused", decision.Reason, StringComparison.Ordinal);
        }

        source.ScanEnabled = true;
        var resumed = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true);
        Assert.True(gate.Evaluate(new SourceMutationRequest { Source = resumed, Mutation = SourceMutationKind.Move, Path = Path.Combine(root, "a.txt") }).Allowed);
    }

    [Fact]
    public void A_folder_write_tags_choice_decides_whether_the_gate_allows_tagging_there()
    {
        var root = Path.Combine(Path.GetTempPath(), "tuvima-write-tags", Guid.NewGuid().ToString("N"));
        var source = new LibrarySourceConfig
        {
            Id = "source",
            Path = root,
            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
            AccessMode = LibrarySourceAccessModes.Writable,
        };
        var library = new LibraryFolderConfig
        {
            Id = "library",
            Sources = [source],
            PrimaryDestinationSourceId = "source",
            OrganizationPolicy = new() { WriteTags = true },
        };

        Assert.True(FileSourceMutationPolicyFactory.Create(library, source).AllowMetadataWriteback);

        source.FileHandlingOverride = new LibrarySourceFileHandlingOverride { WriteTags = false };
        Assert.False(FileSourceMutationPolicyFactory.Create(library, source).AllowMetadataWriteback);

        library.OrganizationPolicy.WriteTags = false;
        source.FileHandlingOverride = new LibrarySourceFileHandlingOverride { WriteTags = true };
        Assert.True(FileSourceMutationPolicyFactory.Create(library, source).AllowMetadataWriteback);
    }
}
