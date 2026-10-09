using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SetupAndLivePresentationTests : AsyncBunitContext
{
    public SetupAndLivePresentationTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void AdministratorExplainsInvalidPasswordAndCanRevealIt()
    {
        var cut = Render<SetupAdministratorStage>(p => p
            .Add(x => x.Email, "owner@example.com")
            .Add(x => x.Password, "seven77")
            .Add(x => x.PasswordConfirmation, "seven77"));
        Assert.NotEmpty(cut.FindAll(".tl-input-error"));
        cut.Find("button[aria-label='Show password']").Click();
        var label = cut.FindAll("label").Single(x => x.TextContent == "Password");
        Assert.Equal("text", cut.Find("#" + label.GetAttribute("for")).GetAttribute("type"));
        cut.Render(p => p.Add(x => x.Password, "mossy-anvil2").Add(x => x.PasswordConfirmation, "mossy-anvil2"));
        Assert.Empty(cut.FindAll(".tl-input-error"));
        Assert.False(cut.FindAll("button").Single(x => x.TextContent.Contains("Create administrator")).HasAttribute("disabled"));
    }

    [Fact]
    public void TypingKeepsTheLivePasswordErrorUntilTheLengthIsValid()
    {
        var cut = Render<SetupAdministratorStage>();
        var label = cut.FindAll("label").Single(x => x.TextContent == "Password");
        var input = cut.Find("#" + label.GetAttribute("for"));
        input.Input("seven77");
        cut.WaitForAssertion(() => Assert.Equal("true", input.GetAttribute("aria-invalid")));
        input.Input("mossy-anvil2");
        cut.WaitForAssertion(() => Assert.Equal("false", input.GetAttribute("aria-invalid")));
    }

    [Fact]
    public void RecoveryCodesUseOneReadOnlyCopyFriendlyField()
    {
        var cut = Render<RecoveryCodesDisplay>(parameters => parameters
            .Add(component => component.Codes, ["alpha-bravo", "charlie-delta"]));

        var field = cut.Find("textarea[readonly]");
        Assert.Contains("alpha-bravo", field.TextContent);
        Assert.Contains("charlie-delta", field.TextContent);
        Assert.Single(cut.FindAll("textarea"));
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Copy all"));
        Assert.NotEmpty(cut.FindAll("a[download]"));
    }

    [Fact]
    public void ReadinessNavigationSharesOneFooterRow()
    {
        var readiness = new SetupReadinessDto(true,
        [
            new SetupCapabilityDto("preflight", "Preflight", "passed", true, "Server capabilities validated.", null),
            new SetupCapabilityDto("media-locations", "Media", "deferred", false, "Libraries can be added later.", "/setup?step=media-locations"),
        ]);
        var cut = Render<SetupReadinessStage>(parameters => parameters
            .Add(component => component.Readiness, readiness));

        var footer = cut.Find(".setup-stage__footer");
        Assert.Contains("Back", footer.TextContent);
        Assert.Contains("Run a sample scan", footer.TextContent);
        Assert.Contains("Finish setup", footer.TextContent);
        Assert.Single(cut.FindAll(".setup-stage__footer"));
    }

    [Fact]
    public void UpdatedLivePreviewPreservesServerActivityOrderAndNewArtwork()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var batch = Guid.NewGuid();
        var before = new IngestionPresentationSnapshotDto { CurrentMedia = [new() { BatchId = batch, GroupId = first }, new() { BatchId = batch, GroupId = second }] };
        var after = new IngestionPresentationSnapshotDto { CurrentMedia = [new() { BatchId = batch, GroupId = second, CoverUrl = "/stream/artwork/new" }, new() { BatchId = batch, GroupId = first }] };
        var merge = typeof(IngestionLiveDashboardState).GetMethod("MergePresentation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var actual = (IngestionPresentationSnapshotDto)merge.Invoke(null, [before, after])!;
        Assert.Equal(second, actual.CurrentMedia[0].GroupId);
        Assert.Equal("/stream/artwork/new", actual.CurrentMedia[0].CoverUrl);
    }
}
