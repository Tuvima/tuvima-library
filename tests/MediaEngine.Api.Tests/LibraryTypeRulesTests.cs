using MediaEngine.Api.Services.Settings;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

public sealed class LibraryTypeRulesTests
{
    private static LibraryFolderConfig Library(string id, string name, params string[] types) => new()
    {
        Id = id,
        Name = name,
        MediaTypes = [.. types],
    };

    private static LibrariesConfiguration Config(params LibraryFolderConfig[] libraries) => new() { Libraries = [.. libraries] };

    [Fact]
    public void SecondLibraryForTheSameTypeIsRejected()
    {
        var current = Config(Library("a", "Movies", "Movies"));
        var proposed = Config(Library("a", "Movies", "Movies"), Library("b", "Films", "Movies"));

        var error = LibraryTypeRules.ValidateOneLibraryPerType(current, proposed);

        Assert.NotNull(error);
        Assert.Contains("already has a library", error);
    }

    [Fact]
    public void TvAndTvShowsCountAsTheSameType()
    {
        var proposed = Config(Library("a", "TV", "TV"), Library("b", "Shows", "TVShows"));

        Assert.NotNull(LibraryTypeRules.ValidateOneLibraryPerType(Config(), proposed));
    }

    [Fact]
    public void OneLibraryPerTypeIsAccepted()
    {
        var proposed = Config(Library("a", "Books", "Books"), Library("b", "Movies", "Movies"));

        Assert.Null(LibraryTypeRules.ValidateOneLibraryPerType(Config(), proposed));
    }

    [Fact]
    public void ExistingDuplicatesDoNotBlockOtherEdits()
    {
        var current = Config(Library("a", "Movies", "Movies"), Library("b", "Films", "Movies"));
        var proposed = Config(Library("a", "Movies", "Movies"), Library("b", "Films", "Movies"), Library("c", "Books", "Books"));

        Assert.Null(LibraryTypeRules.ValidateOneLibraryPerType(current, proposed));
    }

    [Fact]
    public void PersonalLibrariesAreNotChecked()
    {
        var personal = Library("p", "Mine", "Movies");
        personal.Kind = LibraryKinds.Personal;
        var proposed = Config(Library("a", "Movies", "Movies"), personal);

        Assert.Null(LibraryTypeRules.ValidateOneLibraryPerType(Config(), proposed));
    }
}
