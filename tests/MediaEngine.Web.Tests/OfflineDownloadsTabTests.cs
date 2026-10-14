using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class OfflineDownloadsTabTests : AsyncBunitContext
{
    public OfflineDownloadsTabTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void UseJobs(params EncodeJobDto[] jobs)
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetEncodeJobsAsync),
            _ => Task.FromResult(jobs.ToList())));
        Services.AddSingleton(api);
    }

    private static EncodeJobDto Job(string profile, string status, double progress = 0) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        ProfileKey = profile,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        ProgressPct = progress,
    };

    [Fact]
    public void PhoneRendersEachJobAsACardWithRoleAttributesAndKeepsCancel()
    {
        var jobs = new[] { Job("phone-720p", EncodeJobStatuses.Queued), Job("tablet-1080p", EncodeJobStatuses.Running, 42) };
        UseJobs(jobs);

        var cut = Render<OfflineDownloadsTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        Assert.NotEmpty(cut.FindAll(".app-table-shell--cards"));
        foreach (var role in new[] { "title", "subtitle", "value", "actions" })
        {
            Assert.Single(cut.FindAll($"th[data-card-role='{role}']"));
            Assert.Equal(jobs.Length, cut.FindAll($"tbody td[data-card-role='{role}']").Count);
        }

        Assert.Equal("phone-720p", cut.FindAll("tbody td[data-card-role='subtitle']")[0].TextContent.Trim());
        Assert.Equal(2, cut.FindAll("tbody td[data-label='Progress']").Count);
        Assert.Empty(cut.FindAll("tbody td[data-label='Progress'][data-card-role]"));

        var cancelButtons = cut.FindAll("tbody td[data-card-role='actions'] button").Where(button => button.TextContent.Contains("Cancel")).ToList();
        Assert.Equal(jobs.Length, cancelButtons.Count);
        Assert.All(cancelButtons, button => Assert.False(button.HasAttribute("disabled")));
    }

    [Fact]
    public void DesktopKeepsTheSixColumnTableAndCancelIsDisabledForFinishedJobs()
    {
        UseJobs(Job("desktop-4k", EncodeJobStatuses.Complete, 100));

        var cut = Render<OfflineDownloadsTab>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var headers = cut.FindAll("thead th").Select(header => header.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "Asset", "Profile", "Status", "Progress", "Scheduled", "Actions" }, headers);
        Assert.Single(cut.FindAll("tbody tr"));

        var cancel = cut.FindAll("tbody button").Single(button => button.TextContent.Contains("Cancel"));
        Assert.True(cancel.HasAttribute("disabled"));
    }
}
