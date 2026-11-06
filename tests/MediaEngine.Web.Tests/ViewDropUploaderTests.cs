using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Dropping files on Photos: only photos and videos go up, the rest are turned away with a reason, and the ribbon says what happened.</summary>
public sealed class ViewDropUploaderTests : AsyncBunitContext
{
    private readonly List<string> _sent = [];
    private readonly ViewRibbonState _ribbon = new();

    private ViewDropUploader Uploader()
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.UploadViewMediaAsync), arguments =>
        {
            var name = (string)arguments![1]!;
            _sent.Add(name);
            return Task.FromResult(new ViewUploadResult(true, new ViewUploadResponseDto(Guid.NewGuid(), true, 1, 0)));
        }));
        return new ViewDropUploader(api, _ribbon, Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<MediaEngine.Web.Resources.SharedStrings>>());
    }

    private sealed class FakeFile(string name) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => 3;
        public string ContentType => "application/octet-stream";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream([1, 2, 3]);
    }

    [Fact]
    public async Task APdfDroppedOnPhotosIsRejectedWithTheReasonAndNothingIsSent()
    {
        var uploader = Uploader();

        var result = await uploader.UploadAsync([new FakeFile("lease.pdf")]);
        uploader.Report(result);

        Assert.Empty(_sent);
        var rejection = Assert.Single(result.Rejected);
        Assert.Equal("It is a document.", rejection.Reason);
        Assert.True(_ribbon.IsError);
        Assert.Contains("This isn't a valid photo or video.", _ribbon.Message, StringComparison.Ordinal);
        Assert.Contains("lease.pdf", _ribbon.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMixedDropUploadsOnlyThePhotosAndVideosAndSaysSoOnTheRibbon()
    {
        var uploader = Uploader();

        var result = await uploader.UploadAsync([new FakeFile("beach.jpg"), new FakeFile("lease.pdf"), new FakeFile("clip.MOV")]);
        uploader.Report(result);

        Assert.Equal(["beach.jpg", "clip.MOV"], _sent);
        Assert.Equal(2, result.Added);
        Assert.Single(result.Rejected);
        Assert.Contains("Added 2 items.", _ribbon.Message, StringComparison.Ordinal);
        Assert.Contains("lease.pdf", _ribbon.Message, StringComparison.Ordinal);
        Assert.True(_ribbon.IsError);
        Assert.False(_ribbon.InProgress);
    }

    [Fact]
    public async Task ProgressShowsOnTheRibbonWhileFilesGoUpOneAfterAnother()
    {
        var seen = new List<(int? Done, int? Total, string? Message)>();
        _ribbon.Changed += () => seen.Add((_ribbon.ProgressDone, _ribbon.ProgressTotal, _ribbon.Message));
        var uploader = Uploader();

        await uploader.UploadAsync([new FakeFile("a.jpg"), new FakeFile("b.jpg")]);

        Assert.Equal([(0, 2), (1, 2)], seen.Select(entry => (entry.Done!.Value, entry.Total!.Value)));
        Assert.Contains("Adding 2 of 2: b.jpg", seen[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("photo.HEIC", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("notes.docx", false)]
    [InlineData("archive.zip", false)]
    public void OnlyPhotoAndVideoFileTypesAreAccepted(string name, bool accepted) =>
        Assert.Equal(accepted, ViewDropUploader.IsPhotoOrVideo(name));
}
