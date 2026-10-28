using System.Text.Json;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Tests;

public sealed class LibraryFileHandlingTests
{
    private const string A = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string B = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    [Fact]
    public void ANewLibraryIsStandardAccessAndFollowsTheServerForDuplicates()
    {
        var library = new LibraryFolderConfig();

        Assert.Equal(LibraryAccessLevels.Standard, library.Access);
        Assert.Null(library.FileHandling.DuplicatePolicy);
        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, library.ResolveDuplicatePolicy(null, LibraryDuplicatePolicies.KeepBoth));
    }

    [Theory]
    [InlineData("standard", true)]
    [InlineData("selected", true)]
    [InlineData("vault", true)]
    [InlineData("everyone", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AccessIsStandardSelectedOrVaultOnly(string? value, bool valid)
    {
        Assert.Equal(valid, LibraryAccessLevels.IsValid(value));
    }

    [Fact]
    public void ALibraryDuplicateRuleOverridesTheServerOne()
    {
        var library = new LibraryFolderConfig { FileHandling = new LibraryFileHandlingConfig { DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth } };

        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, library.ResolveDuplicatePolicy(null, LibraryDuplicatePolicies.KeepOne));
    }

    [Fact]
    public void AFolderDuplicateRuleOverridesItsLibraryAndTheServer()
    {
        var folder = Source(A);
        folder.FileHandlingOverride = new LibrarySourceFileHandlingOverride { DuplicatePolicy = LibraryDuplicatePolicies.KeepOne };
        var library = new LibraryFolderConfig
        {
            FileHandling = new LibraryFileHandlingConfig { DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth },
            Sources = [folder, Source(B)],
        };

        Assert.Equal(LibraryDuplicatePolicies.KeepOne, library.ResolveDuplicatePolicy(folder, LibraryDuplicatePolicies.KeepBoth));
        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, library.ResolveDuplicatePolicy(library.Sources[1], LibraryDuplicatePolicies.KeepOne));
    }

    [Fact]
    public void AFolderWriteTagsChoiceOverridesTheLibraryOnlyWhenSet()
    {
        Assert.False(LibraryFileHandlingResolver.ResolveWriteTags(libraryWriteTags: true, folderWriteTags: false));
        Assert.True(LibraryFileHandlingResolver.ResolveWriteTags(libraryWriteTags: true, folderWriteTags: null));
        Assert.False(LibraryFileHandlingResolver.ResolveWriteTags(libraryWriteTags: false, folderWriteTags: null));
    }

    [Fact]
    public void AFolderWithScanningOffIsNotScannableButStillCatalogued()
    {
        var paused = Source(A);
        paused.ScanEnabled = false;
        var library = new LibraryFolderConfig { Sources = [paused, Source(B)] };

        Assert.Equal([B], library.ScannableSources.Select(source => source.Id));
        Assert.Equal([A, B], library.CataloguedSources.Select(source => source.Id));
    }

    [Fact]
    public void AFolderMergedAwayIsNeitherScannableNorCatalogued()
    {
        var merged = Source(A);
        merged.MergeState = LibrarySourceMergeStates.Merged;
        var library = new LibraryFolderConfig { Sources = [merged, Source(B)] };

        Assert.Equal([B], library.ScannableSources.Select(source => source.Id));
        Assert.Equal([B], library.CataloguedSources.Select(source => source.Id));
    }

    [Fact]
    public void AFolderScansByDefault()
    {
        Assert.True(new LibrarySourceConfig().ScanEnabled);
        Assert.Null(new LibrarySourceConfig().FileHandlingOverride);
    }

    [Fact]
    public void SavingDropsAnOverrideThatSetsNothingAndKeepsOneThatDoes()
    {
        var empty = Source(A);
        empty.FileHandlingOverride = new LibrarySourceFileHandlingOverride();
        var set = Source(B);
        set.FileHandlingOverride = new LibrarySourceFileHandlingOverride { WriteTags = false };
        var configuration = new LibrariesConfiguration { Libraries = [new LibraryFolderConfig { Sources = [empty, set] }] };

        LibrariesConfigurationNormalizer.Normalize(configuration);

        Assert.Null(empty.FileHandlingOverride);
        Assert.NotNull(set.FileHandlingOverride);
        Assert.False(set.FileHandlingOverride!.WriteTags);
    }

    [Fact]
    public void TheNewFieldsUseTheirWireNamesAndRoundTrip()
    {
        var library = new LibraryFolderConfig
        {
            Access = LibraryAccessLevels.Selected,
            FileHandling = new LibraryFileHandlingConfig { DuplicatePolicy = LibraryDuplicatePolicies.KeepBoth },
            Sources =
            [
                new LibrarySourceConfig
                {
                    Id = A,
                    ScanEnabled = false,
                    FileHandlingOverride = new LibrarySourceFileHandlingOverride { DuplicatePolicy = LibraryDuplicatePolicies.KeepOne, WriteTags = false },
                },
            ],
        };

        var json = JsonSerializer.Serialize(library);
        var parsed = JsonSerializer.Deserialize<LibraryFolderConfig>(json)!;

        Assert.Contains("\"access\":\"selected\"", json, StringComparison.Ordinal);
        Assert.Contains("\"file_handling\":{\"duplicate_policy\":\"keep_both\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"scan_enabled\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"file_handling_override\":{\"duplicate_policy\":\"keep_one\",\"write_tags\":false}", json, StringComparison.Ordinal);
        Assert.Equal(LibraryAccessLevels.Selected, parsed.Access);
        Assert.Equal(LibraryDuplicatePolicies.KeepBoth, parsed.FileHandling.DuplicatePolicy);
        Assert.False(parsed.Sources[0].ScanEnabled);
        Assert.Equal(LibraryDuplicatePolicies.KeepOne, parsed.Sources[0].FileHandlingOverride!.DuplicatePolicy);
        Assert.False(parsed.Sources[0].FileHandlingOverride!.WriteTags);
    }

    [Fact]
    public void TheLibrariesFileVersionIsNinePointZero()
    {
        Assert.Equal("9.0", LibrariesConfiguration.CurrentSchemaVersion);
        Assert.Equal("9.0", new LibrariesConfiguration().SchemaVersion);
    }

    private static LibrarySourceConfig Source(string id) => new()
    {
        Id = id,
        Path = $"/media/{id}",
        ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
        AccessMode = LibrarySourceAccessModes.Writable,
    };
}
