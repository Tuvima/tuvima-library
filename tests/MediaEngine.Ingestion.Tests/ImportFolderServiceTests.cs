using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Tests.Helpers;

namespace MediaEngine.Ingestion.Tests;

public sealed class ImportFolderServiceTests : IDisposable
{
    private readonly ImportFolderHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("Movies", MediaType.Movies)]
    [InlineData("TV", MediaType.TV)]
    [InlineData("tv", MediaType.TV)]
    [InlineData("Music", MediaType.Music)]
    [InlineData("Books", MediaType.Books)]
    [InlineData("Audiobooks", MediaType.Audiobooks)]
    [InlineData("Comics", MediaType.Comics)]
    public void A_file_in_a_per_type_folder_gets_that_type_as_its_prior(string folder, MediaType expected)
    {
        var path = Path.Combine(_h.ImportDir, folder, "Some Show", "episode.mkv");

        Assert.Equal(expected, _h.Importer.ResolveTypePrior(path));
        Assert.Equal(Path.Combine(_h.ImportDir, folder), _h.Importer.ResolveTypeFolder(path), ignoreCase: true);
    }

    [Theory]
    [InlineData("Dune.epub")]
    [InlineData("Other\\Dune.epub")]
    [InlineData(".data\\staging\\Dune.epub")]
    public void A_file_at_the_root_or_in_another_folder_has_no_prior(string relative)
    {
        var path = Path.Combine(_h.ImportDir, relative.Replace('\\', Path.DirectorySeparatorChar));

        Assert.Null(_h.Importer.ResolveTypePrior(path));
    }

    [Fact]
    public void Files_outside_the_Import_folder_have_no_prior_and_are_not_import_files()
    {
        var path = Path.Combine(_h.MoviesDir, "Movies", "x.mkv");

        Assert.Null(_h.Importer.ResolveTypePrior(path));
        Assert.False(_h.Importer.IsInImportFolder(path));
        Assert.False(_h.Importer.IsInImportFolder(_h.ImportDir + "-sibling" + Path.DirectorySeparatorChar + "x.mkv"));
        Assert.True(_h.Importer.IsInImportFolder(Path.Combine(_h.ImportDir, "x.mkv")));
    }

    [Fact]
    public void The_working_folder_is_recognised()
    {
        Assert.True(_h.Importer.IsWorkingPath(Path.Combine(_h.ImportDir, ".data", "staging", "x.mkv")));
        Assert.False(_h.Importer.IsWorkingPath(Path.Combine(_h.ImportDir, "Movies", "x.mkv")));
    }

    [Fact]
    public void Setting_the_folder_creates_the_seven_per_type_folders_and_leaves_existing_files_alone()
    {
        var existing = _h.WriteImportFile(Path.Combine("Movies", "already here.mkv"), "keep me");

        var created = _h.Importer.EnsureSubfolders();

        Assert.Equal(6, created.Count);
        foreach (var name in new[] { "Movies", "TV", "Music", "Books", "Audiobooks", "Comics", "Videos" })
        {
            Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, name)), name);
        }

        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Empty(_h.Importer.EnsureSubfolders());
    }

    [Fact]
    public void The_per_type_folders_and_type_priors_follow_the_loaded_kinds_list_not_the_built_in_one()
    {
        var magazines = LibraryKindsConfiguration.BuiltIn().Single(kind => kind.Key == "comics");
        magazines.Label = "Manga";
        _h.Config.LibraryKinds = new LibraryKindsConfiguration { Kinds = [.. LibraryKindsConfiguration.BuiltIn().Where(kind => kind.Key != "comics"), magazines] };

        var created = _h.Importer.EnsureSubfolders();

        Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, "Manga")));
        Assert.Equal(MediaType.Comics, _h.Importer.ResolveTypePrior(Path.Combine(_h.ImportDir, "Manga", "Akira", "v1.cbz")));
        Assert.Null(_h.Importer.ResolveTypePrior(Path.Combine(_h.ImportDir, "Notes", "v1.cbz")));
        Assert.Contains(Path.Combine(_h.ImportDir, "Manga"), created);
    }

    [Fact]
    public void The_Import_folder_itself_is_only_created_when_asked_so_an_offline_drive_is_never_replaced()
    {
        Directory.Delete(_h.ImportDir, recursive: true);

        Assert.Empty(_h.Importer.EnsureSubfolders());
        Assert.False(Directory.Exists(_h.ImportDir));

        Assert.Equal(7, _h.Importer.EnsureSubfolders(createImportFolder: true).Count);
        Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, "Comics")));
    }

    [Fact]
    public void Removing_the_Import_folder_from_the_settings_deletes_nothing_on_disk()
    {
        var file = _h.WriteImportFile(Path.Combine("Movies", "Dune.mkv"), "dune");
        _h.Importer.EnsureSubfolders();
        _h.Config.Libraries.StorageLocations.Clear();

        Assert.Null(_h.Importer.GetImportPath());
        Assert.Empty(_h.Importer.EnsureSubfolders(createImportFolder: true));
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, "Comics")));
        Assert.False(_h.Importer.IsInImportFolder(file));
        Assert.Null(_h.Importer.CreateImportPolicy());
    }

    [Fact]
    public void Empty_folders_are_removed_but_never_the_Import_folder_a_type_folder_or_the_working_folder()
    {
        _h.Importer.EnsureSubfolders();
        var nested = Path.Combine(_h.ImportDir, "TV", "Show", "Season 1");
        Directory.CreateDirectory(nested);
        var working = Path.Combine(_h.ImportDir, ".data", "staging");
        Directory.CreateDirectory(working);

        _h.Importer.RemoveEmptyFolders(nested);
        _h.Importer.RemoveEmptyFolders(Path.Combine(_h.ImportDir, "Movies"));
        _h.Importer.RemoveEmptyFolders(working);
        _h.Importer.RemoveEmptyFolders(_h.ImportDir);

        Assert.False(Directory.Exists(Path.Combine(_h.ImportDir, "TV", "Show")));
        Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, "TV")));
        Assert.True(Directory.Exists(Path.Combine(_h.ImportDir, "Movies")));
        Assert.True(Directory.Exists(working));
        Assert.True(Directory.Exists(_h.ImportDir));
    }

    [Fact]
    public void A_folder_that_still_holds_something_is_not_removed()
    {
        var file = _h.WriteImportFile(Path.Combine("TV", "Show", "notes.txt"), "keep");

        _h.Importer.RemoveEmptyFolders(Path.GetDirectoryName(file)!);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Empty_folders_outside_the_Import_folder_are_never_removed()
    {
        var outside = Path.Combine(_h.MoviesDir, "Empty");
        Directory.CreateDirectory(outside);

        _h.Importer.RemoveEmptyFolders(outside);

        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void The_destination_is_the_one_folder_marked_New_media_and_is_ready_when_it_can_be_written()
    {
        var destination = _h.Importer.ResolveDestination(MediaType.Movies, requiredBytes: 1024);

        Assert.True(destination.IsReady);
        Assert.Equal(_h.MoviesDir, destination.Source!.Path);
        Assert.Equal(ImportFolderHarness.MoviesLibraryId, destination.Library!.Id);
        Assert.Empty(Directory.GetFiles(_h.MoviesDir)); // the write check leaves nothing behind
    }

    [Fact]
    public void Changing_the_New_media_folder_applies_on_the_next_decision_without_a_restart()
    {
        var other = _h.Config.Libraries.Libraries[0];
        other.PrimaryDestinationSourceId = "movies-other";

        var destination = _h.Importer.ResolveDestination(MediaType.Movies);

        Assert.True(destination.IsReady);
        Assert.Equal(_h.ElsewhereDir, destination.Source!.Path);
    }

    [Fact]
    public void A_type_with_no_library_has_no_managed_folder()
    {
        var destination = _h.Importer.ResolveDestination(MediaType.Comics);

        Assert.Equal(ImportDestinationState.NoManagedFolder, destination.State);
        Assert.False(destination.IsReady);
    }

    [Fact]
    public void An_unknown_type_has_no_destination()
    {
        Assert.Equal(ImportDestinationState.NoManagedFolder, _h.Importer.ResolveDestination(MediaType.Unknown).State);
    }

    [Fact]
    public void A_destination_that_is_offline_or_too_full_is_unavailable_and_never_falls_back()
    {
        Directory.Delete(_h.MoviesDir);

        var offline = _h.Importer.ResolveDestination(MediaType.Movies);
        Assert.Equal(ImportDestinationState.Unavailable, offline.State);
        Assert.False(Directory.Exists(_h.MoviesDir));

        Directory.CreateDirectory(_h.MoviesDir);
        var tooBig = _h.Importer.ResolveDestination(MediaType.Movies, requiredBytes: long.MaxValue / 2);
        Assert.Equal(ImportDestinationState.Unavailable, tooBig.State);
        Assert.Contains("full", tooBig.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_read_only_marked_folder_is_unavailable()
    {
        var library = _h.Config.Libraries.Libraries[0];
        library.Sources.First(source => source.Id == "movies-new").AccessMode = LibrarySourceAccessModes.ReadOnly;

        var destination = _h.Importer.ResolveDestination(MediaType.Movies);

        Assert.Equal(ImportDestinationState.Unavailable, destination.State);
    }

    [Fact]
    public void The_duplicate_rule_is_read_from_the_current_settings()
    {
        Assert.Equal(LibraryDuplicatePolicies.KeepOne, _h.Importer.DuplicatePolicy);
        _h.Config.Libraries.FileHandling.DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth;
        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, _h.Importer.DuplicatePolicy);
    }

    [Fact]
    public void The_Import_folder_policy_allows_moving_out_but_never_being_a_destination()
    {
        var policy = _h.Importer.CreateImportPolicy();
        Assert.NotNull(policy);
        Assert.True(policy.AllowMove);
        Assert.False(policy.AllowDelete);
        Assert.False(policy.AllowAsDestination);
        Assert.True(_h.Importer.CreateImportPolicy(allowDelete: true)!.AllowDelete);
    }

    [Fact]
    public void A_change_to_the_settings_file_applies_on_the_very_next_decision()
    {
        var configDirectory = Path.Combine(_h.Root, "config");
        Directory.CreateDirectory(configDirectory);
        var loader = new MediaEngine.Storage.ConfigurationDirectoryLoader(configDirectory);
        var newMedia = Guid.NewGuid().ToString("D");
        var other = Guid.NewGuid().ToString("D");
        var configuration = new LibrariesConfiguration
        {
            StorageLocations =
            [
                new ServerStorageLocationConfig { Id = "media", Label = "Tuvima", Path = _h.LibraryRoot, AllowWrite = true },
                new ServerStorageLocationConfig { Id = ImportFolderRules.LocationId, Label = "Import", Path = _h.ImportDir, AllowWrite = true },
            ],
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = "Movies",
                    Category = "Movies",
                    Kind = LibraryKinds.Catalogued,
                    KindKey = "movies",
                    MediaTypes = ["Movies"],
                    Sources =
                    [
                        ImportFolderHarness.Managed(newMedia, _h.MoviesDir),
                        ImportFolderHarness.Managed(other, _h.ElsewhereDir),
                    ],
                    PrimaryDestinationSourceId = newMedia,
                },
            ],
        };
        loader.SaveLibraries(configuration);
        var service = new MediaEngine.Ingestion.Services.ImportFolderService(
            loader, Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Ingestion.Services.ImportFolderService>.Instance);

        Assert.Equal(_h.ImportDir, service.GetImportPath());
        Assert.Equal(_h.MoviesDir, service.ResolveDestination(MediaType.Movies).Source!.Path);

        // Mark the other managed folder as New media and save: the next decision uses it, with no restart.
        configuration.Libraries[0].PrimaryDestinationSourceId = other;
        loader.SaveLibraries(configuration);
        Assert.Equal(_h.ElsewhereDir, service.ResolveDestination(MediaType.Movies).Source!.Path);

        // Remove the Import folder and save: it is gone straight away, and nothing was deleted.
        configuration.StorageLocations.RemoveAll(location => location.Id == ImportFolderRules.LocationId);
        loader.SaveLibraries(configuration);
        Assert.Null(service.GetImportPath());
        Assert.True(Directory.Exists(_h.ImportDir));
    }
}
