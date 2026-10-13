using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Tests;

public sealed class LibraryDestinationPolicyTests
{
    private const string A = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string B = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string C = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

    [Fact]
    public void TheFirstManagedFolderAddedBecomesTheDestination()
    {
        var library = Library(Managed(A), Managed(B));

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Equal(A, library.PrimaryDestinationSourceId);
    }

    [Fact]
    public void AnAsIsFolderAddedFirstIsSkippedWhenPickingTheDestination()
    {
        var library = Library(AsIs(A), Managed(B));

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Equal(B, library.PrimaryDestinationSourceId);
    }

    [Fact]
    public void AnExistingDestinationIsKeptEvenWhenAnEarlierManagedFolderExists()
    {
        var library = Library(Managed(A), Managed(B));
        library.PrimaryDestinationSourceId = B;

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Equal(B, library.PrimaryDestinationSourceId);
    }

    [Fact]
    public void WhenTheDestinationIsRemovedTheEarliestRemainingManagedFolderTakesOver()
    {
        var library = Library(Managed(A), Managed(B), Managed(C));
        library.PrimaryDestinationSourceId = A;
        library.Sources.RemoveAll(source => source.Id == A);

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Equal(B, library.PrimaryDestinationSourceId);
    }

    [Fact]
    public void WhenTheDestinationIsSwitchedToAsIsTheEarliestRemainingManagedFolderTakesOver()
    {
        var library = Library(Managed(A), Managed(B), Managed(C));
        library.PrimaryDestinationSourceId = B;
        library.Sources[1].ManagementMode = LibrarySourceManagementModes.ExistingLibrary;

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Equal(A, library.PrimaryDestinationSourceId);
    }

    [Fact]
    public void WithNoManagedFolderLeftThereIsNoDestination()
    {
        var library = Library(Managed(A), AsIs(B));
        library.PrimaryDestinationSourceId = A;
        library.Sources[0].ManagementMode = LibrarySourceManagementModes.ExistingLibrary;

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Null(library.PrimaryDestinationSourceId);
        Assert.Null(library.PrimaryDestination);
    }

    [Fact]
    public void AFolderThatIsConvertingCanStillBeTheDestination()
    {
        var library = Library(Managed(A), Managed(B));
        library.Sources[0].ConversionState = LibrarySourceConversionStates.Converting;
        library.Sources[0].ConversionOperationId = C;
        library.PrimaryDestinationSourceId = A;

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Equal(A, library.PrimaryDestinationSourceId);
    }

    [Fact]
    public void AnAsIsFolderIsNeverReturnedAsTheDestinationEvenIfTheIdPointsAtIt()
    {
        var library = Library(AsIs(A));
        library.PrimaryDestinationSourceId = A;

        Assert.Null(library.PrimaryDestination);
    }

    [Fact]
    public void AccessIsDerivedFromTheManagementMode()
    {
        var library = Library(Managed(A), AsIs(B));
        library.Sources[0].AccessMode = LibrarySourceAccessModes.ReadOnly;
        library.Sources[1].AccessMode = LibrarySourceAccessModes.Writable;

        LibrariesConfigurationNormalizer.Normalize(Wrap(library));

        Assert.Equal(LibrarySourceAccessModes.Writable, library.Sources[0].AccessMode);
        Assert.Equal(LibrarySourceAccessModes.ReadOnly, library.Sources[1].AccessMode);
    }

    [Fact]
    public void ImportDestinationsListTheManagedDestinationPerType()
    {
        var books = Library(Managed(A));
        books.MediaTypes = ["Books"];
        var movies = Library(AsIs(B));
        movies.MediaTypes = ["Movies"];
        var configuration = Wrap(books, movies);
        configuration.StorageLocations.Add(new ServerStorageLocationConfig
        {
            Id = ImportFolderRules.LocationId,
            Label = "Import",
            Path = OperatingSystem.IsWindows() ? @"C:\import" : "/import",
            AllowWrite = true,
        });
        LibrariesConfigurationNormalizer.Normalize(configuration);

        var status = ImportFolderRules.Evaluate(configuration);

        var booksDestination = Assert.Single(status.Destinations, destination => destination.MediaType == "Books");
        Assert.Equal(A, booksDestination.SourceId);
        var moviesDestination = Assert.Single(status.Destinations, destination => destination.MediaType == "Movies");
        Assert.Null(moviesDestination.SourceId);
        Assert.Contains("Movies", status.MissingTypes);
        Assert.False(status.Ready);
    }

    [Theory]
    [InlineData(LibraryDuplicatePolicies.KeepOne, true)]
    [InlineData(LibraryDuplicatePolicies.KeepBoth, false)]
    [InlineData("replace_existing", false)]
    public void OnlyKeepOneRemovesTheIncomingDuplicate(string policy, bool deletes)
    {
        Assert.Equal(deletes, LibraryDuplicatePolicies.DeletesIncomingDuplicate(policy));
    }

    [Theory]
    [InlineData("keep_one", true)]
    [InlineData("keep_both", true)]
    [InlineData("skip_exact", false)]
    [InlineData("replace_existing", false)]
    public void DuplicatePolicyValuesAreKeepOneOrKeepBothOnly(string policy, bool valid)
    {
        Assert.Equal(valid, LibraryDuplicatePolicies.IsValid(policy));
    }

    private static LibrariesConfiguration Wrap(params LibraryFolderConfig[] libraries) => new() { Libraries = [.. libraries] };

    private static LibraryFolderConfig Library(params LibrarySourceConfig[] sources) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = "Test",
        Sources = [.. sources],
    };

    private static LibrarySourceConfig Managed(string id) => new()
    {
        Id = id,
        Path = $"/media/{id}",
        ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
        AccessMode = LibrarySourceAccessModes.Writable,
    };

    private static LibrarySourceConfig AsIs(string id) => new()
    {
        Id = id,
        Path = $"/archive/{id}",
        ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
        AccessMode = LibrarySourceAccessModes.ReadOnly,
    };
}
