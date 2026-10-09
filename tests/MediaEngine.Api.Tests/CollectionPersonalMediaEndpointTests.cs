using System.Text.Json;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Tests;

public sealed class CollectionPersonalMediaEndpointTests
{
    [Fact]
    public void WriteContract_RejectsIndividualAssetIdentityMembers()
    {
        var request = JsonSerializer.Deserialize<CollectionPersonalMediaSourceWriteRequest>("""
            {
              "kind": "gallery",
              "gallery_id": "11111111-1111-1111-1111-111111111111",
              "local_asset_ids": ["22222222-2222-2222-2222-222222222222"]
            }
            """)!;

        var valid = CollectionPersonalMediaService.TryValidateRequest(
            request,
            out _,
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("individual asset IDs", error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(request.AdditionalMembers);
        Assert.Contains("local_asset_ids", request.AdditionalMembers.Keys);
    }

    [Fact]
    public void SmartViewRule_RejectsAssetFieldsAndRequiresCurrentVersion()
    {
        var request = new CollectionPersonalMediaSourceWriteRequest
        {
            Kind = CollectionPersonalMediaSourceKinds.SmartRule,
            RuleVersion = ViewSmartRuleDefinition.CurrentVersion,
            RuleDefinition = new CollectionRuleDefinitionDto
            {
                Version = ViewSmartRuleDefinition.CurrentVersion,
                Groups =
                [
                    new CollectionRuleGroupDto
                    {
                        Conditions =
                        [
                            new CollectionRulePredicateDto
                            {
                                Field = "local_asset_id",
                                Op = "eq",
                                Value = Guid.NewGuid().ToString("D"),
                            },
                        ],
                    },
                ],
            },
        };

        Assert.False(CollectionPersonalMediaService.TryValidateRequest(request, out _, out _, out var error));
        Assert.Contains("View rule field", error, StringComparison.OrdinalIgnoreCase);

        var validGallery = new CollectionPersonalMediaSourceWriteRequest
        {
            Kind = CollectionPersonalMediaSourceKinds.Gallery,
            GalleryId = Guid.NewGuid(),
        };
        Assert.True(CollectionPersonalMediaService.TryValidateRequest(validGallery, out var kind, out _, out _));
        Assert.Equal(CollectionViewSourceKind.Gallery, kind);
    }

    [Fact]
    public void Routes_RequireTrustedProfiles_AdminWrites_AndCountFreeViewerProjection()
    {
        var endpoints = File.ReadAllText(GetRepoFilePath(
            @"src/MediaEngine.Api/Endpoints/CollectionPersonalMediaEndpoints.cs"));
        var service = File.ReadAllText(GetRepoFilePath(
            @"src/MediaEngine.Api/Services/Collections/CollectionPersonalMediaService.cs"));

        Assert.Contains("IViewRequestProfileContext profileContext", endpoints, StringComparison.Ordinal);
        Assert.Contains("GetCollectionPersonalMediaSources", endpoints, StringComparison.Ordinal);
        Assert.Equal(1, Count(endpoints,
            ".RequireClientScope(ApplicationPermissionIds.CollectionsRead.Value)"));
        Assert.Equal(4, Count(endpoints,
            ".RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)"));
        Assert.Equal(4, Count(endpoints, ".RequireCatalogueEntityAccess("));
        Assert.DoesNotContain("RequireAnyRole", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain(".RequireAdmin();", endpoints, StringComparison.Ordinal);
        Assert.Contains("GetAuthorizedProjectionAsync([collectionId], viewerProfileId", service, StringComparison.Ordinal);
        Assert.Contains("AccountFeatureId.View", service, StringComparison.Ordinal);
        Assert.Contains("ApplicationPermissionIds.CollectionsRead", service, StringComparison.Ordinal);
        Assert.Contains("ApplicationPermissionIds.CollectionsWrite", service, StringComparison.Ordinal);
        Assert.Contains("authority.IsEffectiveAdministrator", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Role == ProfileRole.Administrator", service, StringComparison.Ordinal);
        Assert.Contains("CollectionAccessPolicy.CanAccess(collection, viewer)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalAsset", endpoints, StringComparison.Ordinal);

        var properties = typeof(CollectionPersonalMediaSourceDto).GetProperties().Select(property => property.Name).ToList();
        Assert.DoesNotContain(properties, name => name.Contains("Item", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Asset", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, name => name.Contains("Count", StringComparison.OrdinalIgnoreCase));
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string GetRepoFilePath(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }
}
