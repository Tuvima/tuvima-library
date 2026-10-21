using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>Videos is the one catalogued library type that identifies locally only; every other type must stay enriched.</summary>
public sealed class VideosLibraryValidationTests
{
    private static LibrariesConfiguration Shipped()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var json = File.ReadAllText(Path.Combine(dir!, "config", "libraries.json"));
        return JsonSerializer.Deserialize<LibrariesConfiguration>(json)!;
    }

    [Fact]
    public void ShippedConfigurationHasAVideosLibraryThatValidates()
    {
        var config = Shipped();

        Assert.Contains(config.Libraries, library => library.MediaTypes.Contains("Videos"));
        var errors = JsonConfigValidator.Validate(config, "libraries.json");
        Assert.DoesNotContain(errors, error => !error.Contains("absolute path"));
    }

    [Fact]
    public void VideosMayBeLocalOnlyButMoviesMayNot()
    {
        var config = Shipped();
        config.Libraries.Single(library => library.MediaTypes.Contains("Movies")).MetadataPolicy = LibraryMetadataPolicies.LocalOnly;

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("must use enriched metadata"));
        Assert.DoesNotContain(errors, error => error.Contains("videos libraries"));
    }

    [Fact]
    public void AVideosLibraryThatAsksForOutsideLookupsIsRefused()
    {
        var config = Shipped();
        config.Libraries.Single(library => library.MediaTypes.Contains("Videos")).MetadataPolicy = LibraryMetadataPolicies.Enriched;

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("videos libraries must use local_only metadata"));
    }

    [Fact]
    public void VideosMixedWithAnotherTypeIsToldToBeItsOwnLibrary()
    {
        var config = Shipped();
        config.Libraries.Single(library => library.MediaTypes.Contains("Videos")).MediaTypes = ["Videos", "Movies"];

        var errors = JsonConfigValidator.Validate(config, "libraries.json");

        Assert.Contains(errors, error => error.Contains("Videos must be a library of its own"));
    }
}
