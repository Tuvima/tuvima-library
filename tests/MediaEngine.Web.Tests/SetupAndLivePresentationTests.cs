using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
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
            .Add(component => component.Readiness, readiness)
            .Add(component => component.Back, EventCallback.Factory.Create(this, () => { })));

        var footer = cut.Find(".setup-stage__footer");
        Assert.Contains("Back", footer.TextContent);
        Assert.Contains("Open my library", footer.TextContent);
        Assert.Single(cut.FindAll(".setup-stage__footer"));
    }

    [Fact]
    public void ReadinessHidesTheSilentServerCheckAndUsesTheStepNames()
    {
        var readiness = new SetupReadinessDto(true,
        [
            new SetupCapabilityDto("preflight", "Preflight", "passed", true, "Server capabilities validated.", null),
            new SetupCapabilityDto("administrator", "Administrator", "passed", true, "Administrator is ready.", null),
            new SetupCapabilityDto("media-locations", "Media", "deferred", false, "Libraries can be added later.", "/setup?step=media-locations"),
            new SetupCapabilityDto("providers", "Providers", "deferred", false, "Providers can be added later.", "/setup?step=providers"),
        ]);
        var cut = Render<SetupReadinessStage>(parameters => parameters.Add(component => component.Readiness, readiness));

        var names = cut.FindAll(".setup-checks strong").Select(item => item.TextContent.Trim()).ToList();
        Assert.Equal(["Your Account", "Libraries", "Enhance your library"], names);
        Assert.DoesNotContain("Core server capabilities", cut.Markup, StringComparison.Ordinal);
        var links = cut.FindAll(".setup-checks a").Select(link => link.TextContent.Trim()).ToList();
        Assert.Equal(["Set up", "Set up"], links);
        Assert.DoesNotContain("Set up later", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessNamesTheServerRowWithACapitalAndSaysWhatWasSaved()
    {
        var readiness = new SetupReadinessDto(true,
        [
            new SetupCapabilityDto("server", "server", "passed", false,
                "This server is called \"Home\" and shows up on your home network as home.local.", null),
        ]);
        var cut = Render<SetupReadinessStage>(parameters => parameters.Add(component => component.Readiness, readiness));

        Assert.Equal(["Server"], cut.FindAll(".setup-checks strong").Select(item => item.TextContent.Trim()));
        Assert.Contains("shows up on your home network as home.local.", cut.Find(".setup-checks small").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessSubtitleAgreesWithTheHeading()
    {
        var ready = Render<SetupReadinessStage>(parameters => parameters
            .Add(component => component.Readiness, new SetupReadinessDto(true, [new SetupCapabilityDto("administrator", "Administrator", "passed", true, "Ready.", null)])));
        Assert.Contains("You're all set", ready.Markup, StringComparison.Ordinal);
        Assert.Contains("Library is ready to start finding your media.", ready.Markup, StringComparison.Ordinal);

        var blocked = Render<SetupReadinessStage>(parameters => parameters
            .Add(component => component.Readiness, new SetupReadinessDto(false, [new SetupCapabilityDto("administrator", "Administrator", "blocked", false, "Create the first administrator.", "/setup?step=administrator")])));
        Assert.Contains("Almost there", blocked.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Library is ready to start finding your media.", blocked.Markup, StringComparison.Ordinal);
        Assert.Contains("A few things need attention before you open your library.", blocked.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessSubtitleIsNeutralWhileTheCheckIsStillRunning()
    {
        var cut = Render<SetupReadinessStage>();

        Assert.Contains("One moment while Library checks your setup.", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("need attention", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ready to start finding", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstSetupStepHasNoBackButtonAndNoDatabaseEpoch()
    {
        var first = Render<SetupServerStage>(parameters => parameters
            .Add(component => component.ServerName, "Home")
            .Add(component => component.ShowRestore, true)
            .Add(component => component.Backup, new SetupBackupInspectionDto(Guid.NewGuid(), "backup.zip", null, "1", "guid-blob-v1", 10, 20, 3, 2, "This replaces your library.")));

        Assert.DoesNotContain(first.FindAll("button"), button => button.TextContent.Trim() == "Back");
        Assert.DoesNotContain("Database epoch", first.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("guid-blob-v1", first.Markup, StringComparison.Ordinal);
        Assert.Contains("backup.zip", first.Markup, StringComparison.Ordinal);

        // Every later step still has its Back button.
        var later = Render<SetupStageFooter>(parameters => parameters.Add(component => component.Back, EventCallback.Factory.Create(this, () => { })));
        Assert.Contains(later.FindAll("button"), button => button.TextContent.Trim() == "Back");
        var none = Render<SetupStageFooter>();
        Assert.DoesNotContain(none.FindAll("button"), button => button.TextContent.Trim() == "Back");
    }

    [Fact]
    public void UpdatedLivePreviewPreservesServerActivityOrderAndNewArtwork()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var before = new IngestionPresentationSnapshotDto { CurrentMedia = [new() { GroupId = first }, new() { GroupId = second }] };
        var after = new IngestionPresentationSnapshotDto { CurrentMedia = [new() { GroupId = second, CoverUrl = "/stream/artwork/new" }, new() { GroupId = first }] };
        var merge = typeof(IngestionLiveDashboardState).GetMethod("MergePresentation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var actual = (IngestionPresentationSnapshotDto)merge.Invoke(null, [before, after])!;
        Assert.Equal(second, actual.CurrentMedia[0].GroupId);
        Assert.Equal("/stream/artwork/new", actual.CurrentMedia[0].CoverUrl);
    }
}
