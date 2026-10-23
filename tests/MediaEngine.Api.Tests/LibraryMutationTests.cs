using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Api.Tests;

public sealed class LibraryMutationTests
{
    [Fact]
    public void SavingOneLibraryPreservesAnUnrelatedConcurrentEdit()
    {
        var first = new LibraryFolderDto { Id = "first", Name = "Books" };
        var second = new LibraryFolderDto { Id = "second", Name = "Music updated elsewhere" };
        var current = new LibrariesConfigurationDto { Libraries = [first, second] };
        var error = LibraryMutationEndpoints.ApplyEdit(current, new LibraryMutationRequest
        {
            ExpectedLibrary = new LibraryFolderDto { Id = "first", Name = "Books" },
            Library = new LibraryFolderDto { Id = "first", Name = "Reading" },
        });
        Assert.Null(error);
        Assert.Equal("Reading", current.Libraries[0].Name);
        Assert.Same(second, current.Libraries[1]);
        Assert.Equal("Music updated elsewhere", current.Libraries[1].Name);
    }

    [Fact]
    public void ALibraryEditIsNotAConflictJustBecauseTheUnmatchedCountMovedSinceTheScreenReadIt()
    {
        // The Unmatched count is read-only and changes on its own; the screen's copy carries the count it saw.
        var current = new LibrariesConfigurationDto { Libraries = [new LibraryFolderDto { Id = "first", Name = "Movies" }] };

        var error = LibraryMutationEndpoints.ApplyEdit(current, new LibraryMutationRequest
        {
            ExpectedLibrary = new LibraryFolderDto { Id = "first", Name = "Movies", UnmatchedCount = 12 },
            Library = new LibraryFolderDto { Id = "first", Name = "Films", UnmatchedCount = 12 },
        });

        Assert.Null(error);
        Assert.Equal("Films", current.Libraries[0].Name);
        Assert.Equal(0, current.Libraries[0].UnmatchedCount);
    }

    [Fact]
    public void ASavedLibraryListThatCarriesTheUnmatchedCountItWasReadWith_NeverStoresIt()
    {
        var stored = SettingsContractMapper.ToStorage(new UpdateLibrariesRequest
        {
            Libraries = [new LibraryFolderDto { Id = "first", Name = "Movies", UnmatchedCount = 12 }],
        });

        Assert.Null(Assert.Single(stored.Libraries).UnmappedProperties);
    }

    [Fact]
    public void StaleLibraryEditIsRejectedWithoutChangingCurrentState()
    {
        var existing = new LibraryFolderDto { Id = "first", Name = "Newer name" };
        var current = new LibrariesConfigurationDto { Libraries = [existing] };
        var error = LibraryMutationEndpoints.ApplyEdit(current, new LibraryMutationRequest
        {
            ExpectedLibrary = new LibraryFolderDto { Id = "first", Name = "Old name" },
            Library = new LibraryFolderDto { Id = "first", Name = "Stale edit" },
        });
        Assert.NotNull(error);
        Assert.Same(existing, current.Libraries[0]);
    }

    [Fact]
    public void RootMutationCannotReplaceTheLibraryListOrReapplyAStaleRoot()
    {
        var library = new LibraryFolderDto { Id = "first", Name = "Books" };
        var current = new LibrariesConfigurationDto { Libraries = [library] };
        var request = new LibraryMutationRequest
        {
            ExpectedViewStorage = new ViewStorageDto(),
            ViewStorage = new ViewStorageDto { RelativeRoot = "Photos" },
        };
        Assert.Null(LibraryMutationEndpoints.ApplyEdit(current, request));
        Assert.Equal("Photos", current.ViewStorage.RelativeRoot);
        Assert.NotNull(LibraryMutationEndpoints.ApplyEdit(current, request));
        Assert.Same(library, Assert.Single(current.Libraries));
    }
}
