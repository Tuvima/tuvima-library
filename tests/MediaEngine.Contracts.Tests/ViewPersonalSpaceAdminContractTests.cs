using System.Text.Json;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Profiles;

namespace MediaEngine.Contracts.Tests;

public sealed class ViewPersonalSpaceAdminContractTests
{
    [Fact]
    public void ReviewContract_ExposesAdministratorStorageHierarchyWithoutInternalKeysOrDeviceIdentifiers()
    {
        var now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var response = new ViewPersonalSpaceAdminReviewDto(
            Guid.NewGuid(),
            new ViewPersonalSpaceAdminDto(Guid.NewGuid(), "C:\\View\\profiles\\abc", now.AddDays(-2), now),
            [new ViewSourceAdminDto(Guid.NewGuid(), "folder", "Family imports", "linked",
                "D:\\Family imports", true, true, now, now.AddDays(-2), now)],
            [new ViewDeviceAdminDto(Guid.NewGuid(), null, "Shaya's phone", "Example", "Phone", now,
                "complete", now.AddDays(-2), now)]);

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"profile_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"last_activity_at\"", json, StringComparison.Ordinal);
        Assert.Contains("\"root_path\"", json, StringComparison.Ordinal);
        Assert.Contains("\"storage_mode\":\"linked\"", json, StringComparison.Ordinal);
        Assert.Contains("\"last_backup_at\"", json, StringComparison.Ordinal);
        Assert.Contains("\"backup_state\":\"complete\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("library_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source_key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("client_device_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("capacity", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceContract_CarriesTheFolderKindTheSkippedCountWhoAddedItAndWhetherItTakesPhotosAndVideos()
    {
        var now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        var adder = Guid.NewGuid();
        var source = new ViewSourceAdminDto(Guid.NewGuid(), "folder", "Paperwork", "linked", "D:\\Paperwork",
            true, true, now, now.AddDays(-2), now, false, "documents", 3, adder, false);
        var builtIn = source with { SourceType = "browser_upload", ContentKind = "photos", AddedByProfileId = null, AcceptsPhotosAndVideos = true };
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(source, web);

        Assert.Contains("\"content_kind\":\"documents\"", json, StringComparison.Ordinal);
        Assert.Contains("\"skipped_file_count\":3", json, StringComparison.Ordinal);
        Assert.Contains($"\"added_by_profile_id\":\"{adder}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"accepts_photos_and_videos\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"accepts_photos_and_videos\":true", JsonSerializer.Serialize(builtIn, web), StringComparison.Ordinal);
        Assert.Equal(source, JsonSerializer.Deserialize<ViewSourceAdminDto>(json, web));

        // A source from an older Engine, with none of the new names, still reads, as an unnamed Photos folder.
        var older = JsonSerializer.Deserialize<ViewSourceAdminDto>(
            "{\"id\":\"" + Guid.NewGuid() + "\",\"source_type\":\"folder\",\"name\":\"Old\",\"storage_mode\":\"managed\",\"location\":\"x\","
            + "\"include_subdirectories\":true,\"enabled\":true,\"created_at\":\"2026-01-01T00:00:00Z\",\"updated_at\":\"2026-01-01T00:00:00Z\"}", web)!;
        Assert.Equal("photos", older.ContentKind);
        Assert.Equal(0, older.SkippedFileCount);
        Assert.Null(older.AddedByProfileId);
    }

    [Fact]
    public void CreateAndUpdateRequests_NameTheKindAndTheAddedByFilterListHasProfilesWithCounts()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var create = JsonSerializer.Serialize(new CreateViewSourceRequest("Scans", "managed", ContentKind: "documents"), web);
        var update = JsonSerializer.Serialize(new UpdateViewSourceRequest("Scans", true, ContentKind: "photos"), web);
        var profile = Guid.NewGuid();
        var added = JsonSerializer.Serialize(new ViewAddedByDto(profile, "Jess", 12), web);

        Assert.Contains("\"content_kind\":\"documents\"", create, StringComparison.Ordinal);
        Assert.Contains("\"content_kind\":\"photos\"", update, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<CreateViewSourceRequest>("{\"name\":\"x\",\"storage_mode\":\"managed\"}", web)!.ContentKind);
        Assert.Equal($"{{\"profile_id\":\"{profile}\",\"name\":\"Jess\",\"item_count\":12}}", added);
    }

    [Fact]
    public void AssetAndPolicyContracts_CarryTheItemKindTheAddedByNameAndThePerKindSwitches()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var adder = Guid.NewGuid();
        var asset = JsonSerializer.Serialize(new LocalAssetDto(
            Guid.NewGuid(), Guid.NewGuid(), null, null, "image", "Beach", "beach.jpg", "image/jpeg", null,
            DateTimeOffset.Parse("2026-10-22T12:00:00Z"), null, null, null, null, null, null, null, null, null,
            false, false, null, null, 1, [], [], "/t", "/c",
            ContentKind: "photos", AddedByProfileId: adder, AddedByName: "Jess"), web);
        var policy = JsonSerializer.Serialize(new ViewProfilePolicyDto { AllowPhotos = false, AllowPersonalVideos = true }, web);
        var request = JsonSerializer.Deserialize<UpdateViewProfilePolicyRequest>("{\"view_enabled\":true}", web)!;

        Assert.Contains("\"content_kind\":\"photos\"", asset, StringComparison.Ordinal);
        Assert.Contains($"\"added_by_profile_id\":\"{adder}\"", asset, StringComparison.Ordinal);
        Assert.Contains("\"added_by_name\":\"Jess\"", asset, StringComparison.Ordinal);
        Assert.Contains("\"allow_photos\":false", policy, StringComparison.Ordinal);
        Assert.Contains("\"allow_personal_videos\":true", policy, StringComparison.Ordinal);
        // A request that leaves a switch out keeps the saved one, so an older client cannot turn a kind on by accident.
        Assert.Null(request.AllowPhotos);
        Assert.Null(request.AllowPersonalVideos);
        Assert.Contains("\"can_view_photos\":true", JsonSerializer.Serialize(
            new ViewScopeResolutionDto(new ViewResolvedScopeDto(Domain.PersonalMedia.ViewScopeKind.Mine, null, false), []), web),
            StringComparison.Ordinal);
    }
}
