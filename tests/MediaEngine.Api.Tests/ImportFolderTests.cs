using MediaEngine.Api.Services.Settings;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

public sealed class ImportFolderTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "tuvima-import-rules");

    private static LibraryFolderConfig Library(string name, string type, params LibrarySourceConfig[] sources) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = name,
        MediaTypes = [type],
        Sources = [.. sources],
    };

    private static LibrarySourceConfig Managed(string folder) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Path = Path.Combine(Root, folder),
        ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
        AccessMode = LibrarySourceAccessModes.Writable,
    };

    private static LibrarySourceConfig AsIs(string folder) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Path = Path.Combine(Root, folder),
        ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
        AccessMode = LibrarySourceAccessModes.ReadOnly,
    };

    private static LibrariesConfiguration AllTypes(Func<string, LibrarySourceConfig> source, string? importFolder = "import") =>
        LibrariesConfigurationNormalizer.Normalize(new LibrariesConfiguration
        {
            StorageLocations = importFolder is null ? [] :
                [new ServerStorageLocationConfig { Id = ImportFolderRules.LocationId, Label = "Import", Path = Path.Combine(Root, importFolder), AllowWrite = true }],
            Libraries = [.. ImportFolderRules.Types.Select(type => Library(type.Label, type.MediaTypes[0], source(type.Label)))],
        });

    [Fact]
    public void NoImportFolderMeansNothingToCheck()
    {
        var status = ImportFolderRules.Evaluate(AllTypes(label => AsIs(label), importFolder: null));

        Assert.False(status.Configured);
        Assert.False(status.Ready);
        Assert.Empty(status.MissingTypes);
    }

    [Fact]
    public void EveryTypeNeedsADestinationBeforeImportIsReady()
    {
        var status = ImportFolderRules.Evaluate(AllTypes(label => AsIs(label)));

        Assert.True(status.Configured);
        Assert.False(status.Ready);
        Assert.Equal(["Books", "Audiobooks", "Movies", "TV Shows", "Music", "Comics", "Videos"], status.MissingTypes);
    }

    [Fact]
    public void OnlyFoldersTuvimaManagesCountAsDestinations()
    {
        var config = AllTypes(label => label is "Books" or "Movies" ? Managed(label) : AsIs(label));

        var status = ImportFolderRules.Evaluate(config);

        Assert.False(status.Ready);
        Assert.Equal(["Audiobooks", "TV Shows", "Music", "Comics", "Videos"], status.MissingTypes);
    }

    [Fact]
    public void ImportIsReadyWhenEveryTypeHasAManagedDestination()
    {
        var config = AllTypes(label => Managed(label));

        var status = ImportFolderRules.Evaluate(config);

        Assert.True(status.Ready);
        Assert.Equal(7, status.Destinations.Count);
        Assert.All(status.Destinations, destination =>
        {
            Assert.True(destination.HasDestination);
            Assert.Equal(Path.Combine(Root, destination.MediaType), destination.Path);
        });
    }

    [Fact]
    public void TheDestinationIsTheFirstManagedFolderEvenWhenAnAsIsFolderComesFirst()
    {
        var config = AllTypes(label => AsIs(label + "-old"));
        var books = config.Libraries.Single(library => library.Name == "Books");
        books.Sources.Add(Managed("Books"));
        books.Sources.Add(Managed("Books-second"));
        LibrariesConfigurationNormalizer.Normalize(config);

        var status = ImportFolderRules.Evaluate(config);

        Assert.Equal(Path.Combine(Root, "Books"), status.Destinations.Single(destination => destination.MediaType == "Books").Path);
    }

    [Fact]
    public void ContractCarriesTheDestinationOfEachType()
    {
        var config = AllTypes(label => label == "Movies" ? AsIs(label) : Managed(label));

        var dto = MediaEngine.Api.Endpoints.SettingsContractMapper.ToContract(config);

        Assert.Equal(7, dto.ImportFolderStatus.Destinations.Count);
        Assert.Null(dto.ImportFolderStatus.Destinations.Single(destination => destination.MediaType == "Movies").DestinationSourceId);
        Assert.NotNull(dto.ImportFolderStatus.Destinations.Single(destination => destination.MediaType == "Books").DestinationSourceId);
        Assert.Equal(["Movies"], dto.ImportFolderStatus.MissingTypes);
    }

    [Fact]
    public void ATypeWithoutALibraryIsReportedMissing()
    {
        var config = AllTypes(label => Managed(label));
        config.Libraries.RemoveAll(library => library.Name == "Comics");

        var status = ImportFolderRules.Evaluate(config);

        Assert.Equal(["Comics"], status.MissingTypes);
    }

    [Fact]
    public void ImportFolderMustBeSeparateFromLibraryFolders()
    {
        var inside = AllTypes(label => AsIs(label), importFolder: Path.Combine("Books", "incoming"));
        var contains = AllTypes(label => AsIs(label), importFolder: "");
        var separate = AllTypes(label => AsIs(label));

        Assert.Contains("overlap", ImportFolderValidation.Validate(inside));
        Assert.Contains("overlap", ImportFolderValidation.Validate(contains));
        Assert.Null(ImportFolderValidation.Validate(separate));
    }

    [Fact]
    public void ALibraryAtADriveRootOverlapsTheImportFolder()
    {
        var driveRoot = Path.GetPathRoot(Root)!;
        var config = AllTypes(label => AsIs(label));
        config.Libraries[0].Sources[0].Path = driveRoot;

        Assert.Contains("overlap", ImportFolderValidation.Validate(config));
    }

    [Fact]
    public void ImportFolderMustBeAbsoluteAndWritable()
    {
        var relative = AllTypes(label => AsIs(label));
        relative.StorageLocations[0].Path = "import";
        var readOnly = AllTypes(label => AsIs(label));
        readOnly.StorageLocations[0].AllowWrite = false;

        Assert.Contains("absolute", ImportFolderValidation.Validate(relative));
        Assert.Contains("writable", ImportFolderValidation.Validate(readOnly));
    }
}
