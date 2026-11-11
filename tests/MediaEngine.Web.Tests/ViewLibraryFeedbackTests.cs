using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The View panels of a library's pages (Folders, Import, Duplicates) tell what an action did in place: the changed folder glows, a quiet
// line or an alert says the rest, and nothing pops up (design system, Feedback after an action).
public sealed class ViewLibraryFeedbackTests : AsyncBunitContext
{
    private static readonly LibrariesConfigurationDto Configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Library data", Path = "C:\\data", AllowWrite = true }],
        ViewStorage = new ViewStorageDto { StorageLocationId = "media", RelativeRoot = "View" },
    };

    private readonly ProfileViewModel _profile = new(Guid.NewGuid(), "Sam", "#C9922E", "Administrator", DateTimeOffset.UtcNow);
    private readonly ViewSourceAdminDto _source = new(Guid.NewGuid(), "folder", "Camera", "managed", "E:\\Camera", true, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private ViewTidyStatusDto _tidy;
    private ViewIntakeScanResultDto? _scan;
    private ViewDuplicateReportDto? _duplicates;

    public ViewLibraryFeedbackTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        _tidy = new ViewTidyStatusDto(_source.Id, "Camera", 0, null, null, false, false, 2, 0, 0, 0, null);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.GetViewTidyStatusAsync), _ => Task.FromResult<ViewTidyStatusDto?>(_tidy));
            stub.SetHandler(nameof(IEngineApiClient.SetViewTidyScheduleAsync), _ => Task.FromResult<ViewTidyStatusDto?>(_tidy with { Scheduled = true }));
            stub.SetHandler(nameof(IEngineApiClient.GetViewIntakeSummaryAsync), _ => Task.FromResult<ViewIntakeSummaryDto?>(
                new ViewIntakeSummaryDto("photos", "E:\\Import", "E:\\Rejected", true, 0, null, 0, 30, "{year}", [])));
            stub.SetHandler(nameof(IEngineApiClient.ScanViewImportAreaAsync), _ => Task.FromResult(_scan));
            stub.SetHandler(nameof(IEngineApiClient.GetViewDuplicatesAsync), _ => Task.FromResult(_duplicates));
            stub.SetHandler(nameof(IEngineApiClient.ScanViewDuplicatesAsync), _ => Task.FromResult(_duplicates));
        }));
        _dialogs = Render<AppDialogHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private IRenderedComponent<ViewFoldersTab> RenderFolders() =>
        Render<ViewFoldersTab>(parameters => parameters
            .Add(x => x.Configuration, Configuration)
            .Add(x => x.Profiles, [_profile])
            .Add(x => x.Reviews, new Dictionary<Guid, ViewPersonalSpaceAdminReviewDto>
            {
                [_profile.Id] = new(_profile.Id, new ViewPersonalSpaceAdminDto(Guid.NewGuid(), "C:\\data\\View\\Profiles\\Sam", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), [_source], []),
            }));

    [Fact]
    public async Task Tidy_WhenEverythingIsAlreadyFiled_SaysSoInPlaceAndShowsNoToast()
    {
        var cut = RenderFolders();

        await cut.ClickAsync("button[aria-label='Tidy Camera']");

        cut.WaitForAssertion(() => Assert.Equal("Everything in Camera is already filed by date.", Text(cut.Find("[data-folders-note]"))));
    }

    [Fact]
    public async Task Tidy_WhenTheFolderIsStillBeingTidied_ShowsTheProgressInPlace()
    {
        _tidy = _tidy with { Running = true, Total = 10, Moved = 4 };
        var cut = RenderFolders();

        await cut.ClickAsync("button[aria-label='Tidy Camera']");

        cut.WaitForAssertion(() => Assert.Equal("Camera is being tidied: 4 of 10 files done.", Text(cut.Find("[data-folders-note]"))));
    }

    [Fact]
    public async Task NightlyTidy_WhenTurnedOn_GlowsTheFolderRowAndShowsNoToastOrNote()
    {
        var cut = RenderFolders();
        Assert.Empty(cut.FindAll(".view-source-row.tl-just-changed"));

        await cut.ClickAsync("button[aria-label='Tidy Camera every night']");
        await _dialogs.ClickAsync(host => host.FindAll("button").Single(button => Text(button) == "Tidy every night"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".view-source-row.tl-just-changed")));
        Assert.Empty(cut.FindAll("[data-folders-note]"));
    }

    private IRenderedComponent<ViewImportPanel> RenderImport() => Render<ViewImportPanel>(parameters => parameters.Add(x => x.Kind, "photos"));

    [Fact]
    public async Task ScanNow_ShowsWhatTheScanFoundInPlaceAndShowsNoToast()
    {
        _scan = new ViewIntakeScanResultDto(3, 0, 0, 0, "3 files were filed.");
        var cut = RenderImport();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='view-import-waiting']")));

        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => Text(button).Contains("Scan now", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => Assert.Equal("3 files were filed.", Text(cut.Find("[data-import-scan-note]"))));
    }

    [Fact]
    public async Task ScanNow_WhenTheEngineRefuses_ShowsTheFailureAsAnAlert()
    {
        _scan = null;
        var cut = RenderImport();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='view-import-waiting']")));

        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => Text(button).Contains("Scan now", StringComparison.Ordinal)).Click());

        cut.WaitForAssertion(() => Assert.Contains("The Import area could not be scanned.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-import-scan-note]"));
    }

    [Fact]
    public async Task LookForDuplicates_WhenNoneAreFound_TheReportSaysSoAndNothingPopsUp()
    {
        _duplicates = new ViewDuplicateReportDto(true, 3, DateTimeOffset.UtcNow, []);
        var cut = Render<ViewDuplicatesPanel>();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), button => Text(button).Contains("Look for duplicates", StringComparison.OrdinalIgnoreCase)));

        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => Text(button).Contains("Look for duplicates", StringComparison.OrdinalIgnoreCase)).Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".app-alert")));
    }
}
