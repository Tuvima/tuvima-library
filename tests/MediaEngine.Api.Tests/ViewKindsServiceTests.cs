using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Tests.ViewLibraryServiceTests;

namespace MediaEngine.Api.Tests;

// Library setup WP8a: what each View folder accepts, what an item records about its kind and who added it.
public sealed class ViewKindsServiceTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    private static LocalAssetDto[] Items(ViewFixture fixture) =>
        fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 50, IncludeHidden: true)).Items.ToArray();

    private static async Task<ViewSource> FolderAsync(ViewFixture fixture) =>
        Assert.Single(await fixture.Spaces.GetSourcesAsync(fixture.PersonalSpaceId), source => source.SourceKey == "test:personal");

    [Fact]
    public async Task APhotosFolderIndexesPicturesSkipsOtherFilesCountsThemAndLeavesAllOfThemUntouched()
    {
        using var fixture = new ViewFixture();
        var first = fixture.WritePersonal("one.jpg", Jpeg);
        var second = fixture.WritePersonal("two.jpeg", [0xFF, 0xD8, 0xFF, 0xD9, 1]);
        var video = fixture.WritePersonal("clip.mp4", [5, 6, 7]);
        var pdf = fixture.WritePersonal("lease.pdf", DocumentFixtures.Pdf("lease"));
        var before = new[] { first, second, video, pdf }.ToDictionary(path => path, System.IO.File.ReadAllBytes);

        var result = Assert.IsType<LocalAssetScanResultDto>(await fixture.Service.ScanAsync(fixture.PersonalLibraryId));

        Assert.Equal(2, result.ItemsAdded);
        var items = Items(fixture);
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(ViewContentKinds.Photos, item.ContentKind));
        Assert.Equal(2, (await FolderAsync(fixture)).SkippedFileCount);
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, System.IO.File.ReadAllBytes(path));
        }

        // The count starts over on every full scan: once the video is gone, one file is left over.
        System.IO.File.Delete(video);
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        Assert.Equal(1, (await FolderAsync(fixture)).SkippedFileCount);
    }

    [Fact]
    public async Task APersonalVideosFolderTakesVideosAndSkipsPicturesAndDocuments()
    {
        using var fixture = new ViewFixture(contentKind: ViewContentKinds.PersonalVideos);
        fixture.WritePersonal("clip.mp4", [5, 6, 7]);
        fixture.WritePersonal("photo.jpg", Jpeg);
        fixture.WritePersonal("lease.pdf", DocumentFixtures.Pdf("lease"));
        fixture.WritePersonal("song.mp3", [1, 2, 3]);

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        var item = Assert.Single(Items(fixture));
        Assert.Equal(LocalAssetMediaKinds.Video, item.MediaKind);
        Assert.Equal(ViewContentKinds.PersonalVideos, item.ContentKind);
        Assert.Equal(3, (await FolderAsync(fixture)).SkippedFileCount);
    }

    [Fact]
    public async Task ADocumentsFolderTakesPdfsAndPicturesOfPaperAsDocuments()
    {
        using var fixture = new ViewFixture(contentKind: ViewContentKinds.Documents);
        fixture.WritePersonal("lease.pdf", DocumentFixtures.Pdf("lease"));
        fixture.WritePersonal("receipt.jpg", Jpeg);
        fixture.WritePersonal("clip.mp4", [5, 6, 7]);

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        var items = Items(fixture);
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(ViewContentKinds.Documents, item.ContentKind));
        Assert.Contains(items, item => item.MediaKind == LocalAssetMediaKinds.Document);
        Assert.Contains(items, item => item.MediaKind == LocalAssetMediaKinds.Image);
        Assert.Equal(1, (await FolderAsync(fixture)).SkippedFileCount);
    }

    [Fact]
    public async Task TheBuiltInUploadSourceTakesVideosAsPersonalVideosAndPicturesAsPhotos_ButNeverAudio()
    {
        using var fixture = new ViewFixture();
        await using var mp4 = new MemoryStream([5, 6, 7]);
        await using var jpeg = new MemoryStream(Jpeg);
        await using var pdf = new MemoryStream(DocumentFixtures.Pdf("lease"));

        var video = await fixture.Service.UploadAsync(fixture.OwnerProfileId, "clip.mp4", mp4);
        var photo = await fixture.Service.UploadAsync(fixture.OwnerProfileId, "pic.jpg", jpeg);
        var document = await fixture.Service.UploadAsync(fixture.OwnerProfileId, "lease.pdf", pdf);

        Assert.Equal(ViewContentKinds.PersonalVideos, fixture.Repository.Find(video.ItemId)!.ContentKind);
        Assert.Equal(ViewContentKinds.Photos, fixture.Repository.Find(photo.ItemId)!.ContentKind);
        Assert.Equal(ViewContentKinds.Documents, fixture.Repository.Find(document.ItemId)!.ContentKind);
        await using var audio = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.UploadAsync(fixture.OwnerProfileId, "voice.mp3", audio));

        var uploads = Assert.Single(await fixture.Spaces.GetSourcesAsync(fixture.PersonalSpaceId),
            source => source.SourceKey == ViewKindAcceptance.BrowserUploadSourceKey);
        Assert.True(ViewKindAcceptance.IsIntakeSource(uploads.SourceKey));
        Assert.Equal(ViewContentKinds.Photos, uploads.ContentKind);
    }

    [Fact]
    public async Task AnUploadIsRecordedAsAddedByTheProfileMakingTheRequest()
    {
        using var fixture = new ViewFixture();
        await using var behalf = new MemoryStream(Jpeg);
        await using var own = new MemoryStream([0xFF, 0xD8, 0xFF, 0xD9, 9]);

        // A paired phone backs up into the owner's space while the administrator is the one asking.
        var backedUp = await fixture.Service.UploadAsync(
            fixture.OwnerProfileId, "phone.jpg", behalf, addedByProfileId: fixture.AdministratorProfileId);
        var ownUpload = await fixture.Service.UploadAsync(fixture.OwnerProfileId, "mine.jpg", own);

        Assert.Equal(fixture.AdministratorProfileId, fixture.Repository.Find(backedUp.ItemId)!.AddedByProfileId);
        Assert.Equal("Folder administrator", fixture.Repository.Find(backedUp.ItemId)!.AddedByName);
        Assert.Equal(fixture.OwnerProfileId, fixture.Repository.Find(ownUpload.ItemId)!.AddedByProfileId);
    }

    [Fact]
    public async Task AFolderAddedByTheAdministratorTagsEveryFileFoundInItWithThem()
    {
        using var fixture = new ViewFixture(addedByAdministrator: true);
        fixture.WritePersonal("one.jpg", Jpeg);
        fixture.WritePersonal("two.jpg", [0xFF, 0xD8, 0xFF, 0xD9, 1]);

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        var items = Items(fixture);
        Assert.Equal(2, items.Length);
        Assert.All(items, item =>
        {
            Assert.Equal(fixture.AdministratorProfileId, item.AddedByProfileId);
            Assert.Equal("Folder administrator", item.AddedByName);
        });
    }

    [Fact]
    public void WhatEachKindAcceptsIsOneRule()
    {
        Assert.True(ViewKindAcceptance.Accepts(ViewContentKinds.Photos, "image"));
        Assert.False(ViewKindAcceptance.Accepts(ViewContentKinds.Photos, "video"));
        Assert.True(ViewKindAcceptance.Accepts(ViewContentKinds.PersonalVideos, "video"));
        Assert.False(ViewKindAcceptance.Accepts(ViewContentKinds.PersonalVideos, "image"));
        Assert.True(ViewKindAcceptance.Accepts(ViewContentKinds.Documents, "document"));
        Assert.True(ViewKindAcceptance.Accepts(ViewContentKinds.Documents, "image"));
        Assert.False(ViewKindAcceptance.Accepts(ViewContentKinds.Documents, "video"));
        foreach (var kind in ViewContentKinds.All)
        {
            Assert.False(ViewKindAcceptance.Accepts(kind, "audio"));
            Assert.False(ViewKindAcceptance.Accepts(kind, "other"));
        }

        Assert.True(ViewKindAcceptance.SourceAccepts(ViewContentKinds.Photos, ViewKindAcceptance.BrowserUploadSourceKey, "video"));
        Assert.False(ViewKindAcceptance.SourceAccepts(ViewContentKinds.Photos, ViewKindAcceptance.BrowserUploadSourceKey, "audio"));
        Assert.Equal(ViewContentKinds.PersonalVideos, ViewKindAcceptance.KindFor(ViewContentKinds.Photos, ViewKindAcceptance.BrowserUploadSourceKey, "video"));
        Assert.Equal(ViewContentKinds.Documents, ViewKindAcceptance.KindFor(ViewContentKinds.Documents, "test:folder", "image"));
    }
}
