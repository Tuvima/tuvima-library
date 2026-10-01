using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Api.Tests;

public sealed class AssetRenditionEndpointRouteTests
{
    [Fact]
    public void RenditionRoutesAreAuthorizedAndKeepEditionIdentityOutOfDeliveryPreferences()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api",
            "Endpoints", "MetadataEndpoints.AssetRenditions.cs"));

        Assert.Contains("/assets/{assetId:guid}/renditions", source, StringComparison.Ordinal);
        Assert.Contains("/assets/{assetId:guid}/rendition", source, StringComparison.Ordinal);
        Assert.Contains("EvaluateAssetAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpdateRenditionAsync", source, StringComparison.Ordinal);
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)",
            source, StringComparison.Ordinal);

        var properties = typeof(UpdateMediaAssetRenditionRequestDto).GetProperties()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DerivedFromAssetId", properties);
        Assert.DoesNotContain("Preferred", properties);
        Assert.DoesNotContain("WorkId", properties);
        Assert.DoesNotContain("EditionId", properties);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
