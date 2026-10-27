using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Models;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A naming template names the path inside a type's own folder, so every route joins it onto that folder
/// and the category is never written twice (<c>library/Books/Books/...</c>).
/// </summary>
public sealed class LibraryOrganizationPathTests
{
    private static readonly string Library = Path.Combine("data", "library");

    [Theory]
    [InlineData(MediaType.Books, "Books")]
    [InlineData(MediaType.Movies, "Movies")]
    [InlineData(MediaType.TV, "TV")]
    [InlineData(MediaType.Music, "Music")]
    [InlineData(MediaType.Videos, "Videos")]
    public void TypeFolder_WithNoOwningLibrary_IsTheCategoryFolderInsideTheLibraryFolder(MediaType type, string category)
    {
        var folder = LibraryOrganizationPath.TypeFolder(null, Library, type);

        Assert.Equal(Path.Combine(Library, category), folder);
    }

    [Fact]
    public void TypeFolder_WithAnOwningLibrary_IsItsNewMediaFolder()
    {
        var newMedia = Path.Combine("data", "library", "Books");
        var library = new LibraryFolderEntry
        {
            PrimaryDestinationSourceId = "new-media",
            Sources = [new LibrarySourceEntry
            {
                Id = "new-media",
                Path = newMedia,
                ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
            }],
        };

        Assert.Equal(newMedia, LibraryOrganizationPath.TypeFolder(library, Library, MediaType.Books));
    }

    [Fact]
    public void ABookFiledByTheStandardTemplate_LandsInTheBooksFolderWithoutADoubledSegment()
    {
        var template = new IngestionOptions().ResolveTemplate("Books");
        var typeFolder = LibraryOrganizationPath.TypeFolder(null, Library, MediaType.Books);

        var target = Path.Combine(typeFolder, template.Replace("{Author}", "Frank Herbert").Replace('/', Path.DirectorySeparatorChar));

        Assert.StartsWith(Path.Combine(Library, "Books", "Frank Herbert"), target);
        Assert.DoesNotContain(Path.Combine("Books", "Books"), target);
    }

    [Theory]
    [InlineData("Books")]
    [InlineData("Audiobooks")]
    [InlineData("Movies")]
    [InlineData("TV")]
    [InlineData("Comics")]
    [InlineData("Music")]
    [InlineData("Videos")]
    public void TheBuiltInTemplates_NeverStartWithTheCategory(string type)
    {
        var template = new IngestionOptions().ResolveTemplate(type);

        Assert.DoesNotContain("{Category}", template, StringComparison.Ordinal);
        Assert.False(template.StartsWith(type + "/", StringComparison.OrdinalIgnoreCase), template);
    }

    [Fact]
    public void FilingCategory_ForAnUnknownType_IsOther_SoTheGateHoldsTheFile()
    {
        Assert.Equal(LibraryOrganizationPath.UnknownCategory, LibraryOrganizationPath.FilingCategory(null, null));
    }

    [Fact]
    public void FilingCategory_ForAKeepOriginalNamesLibrary_IsNull_SoTheGateNeverHoldsIt()
    {
        var library = new LibraryFolderEntry { OrganizationMode = LibraryOrganizationModes.KeepOriginalNames };

        Assert.Null(LibraryOrganizationPath.FilingCategory(library, null));
    }

    [Fact]
    public void TheOrganizationGate_HoldsAnUnknownTypeButNotAKeepOriginalNamesFile()
    {
        var gate = new OrganizationGate(new ScoringConfiguration());
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["title"] = "Dune" };

        var held = gate.Evaluate(1.0, metadata, hasUserLock: false, mediaTypeNeedsReview: false,
            LibraryOrganizationPath.UnknownCategory);
        var kept = gate.Evaluate(1.0, metadata, hasUserLock: false, mediaTypeNeedsReview: false, filingCategory: null);

        Assert.False(held.CanOrganize);
        Assert.True(kept.CanOrganize);
    }
}
