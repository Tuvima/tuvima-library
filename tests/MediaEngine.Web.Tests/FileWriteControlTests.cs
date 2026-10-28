using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The one write-back control (universal editor spec D15, WP10): a library's "Save changes into the files" setting, shown in Review changes
/// as a read-only "Files will be updated" line with a link to Settings, plus one checkbox for this save only when the library updates its
/// files. Every way into the editor saves through Review changes, so the control must be identical from a detail page's editing bar and a
/// list's selection bar.
/// </summary>
public sealed class FileWriteControlTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<EditSessionRequest> _saves = [];
    private readonly IRenderedComponent<AppPopoverHost> _popovers;
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private bool? _masterSwitch = true;
    private bool _libraryWritesTags = true;

    public FileWriteControlTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(Api());
        _popovers = Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
    }

    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
        stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        stub.SetHandler(nameof(IEngineApiClient.GetFileWriteScheduleAsync), _ =>
            Task.FromResult(_masterSwitch is { } enabled ? new FileWriteScheduleDto { Enabled = enabled } : null));
        stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(new LibrariesConfigurationDto
        {
            Libraries =
            [
                new LibraryFolderDto
                {
                    Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    Name = "Movies",
                    MediaTypes = ["Movies"],
                    OrganizationPolicy = new LibraryOrganizationPolicyDto { WriteTags = _libraryWritesTags },
                },
            ],
        }));
        stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
        {
            var request = (EditSessionRequest)args![0]!;
            _saves.Add(request);
            var results = request.Changes
                .Select(change => new EditSessionChangeResult { Id = change.Id, Kind = change.Kind, EntityId = change.EntityId, Status = EditSessionChangeStatuses.Ok })
                .ToList();
            return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
            {
                PreviewOnly = request.PreviewOnly,
                Valid = true,
                DataSaved = !request.PreviewOnly,
                Changes = results,
            }));
        });
    });

    private async Task<IReviewSource> DetailPageSourceAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(MovieId, "Movies", "movie", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(
            session,
            mode,
            new EditSessionSaver(api),
            () => [new EditTrailItem(MovieId, "Dune: Part One", "Movie")],
            () => Task.FromResult(false))
        {
            MediaTypes = () => ["Movies"],
        };
        Assert.True(await mode.EnterAsync());
        session.Pending.SetField(MovieId, "title", "Dune 2");
        return flow;
    }

    private IReviewSource ListSource()
    {
        var api = Services.GetRequiredService<IEngineApiClient>();
        var list = new BulkEditSession(new PendingEditList(), new EditSessionSaver(api), () => Task.FromResult(false))
        {
            ItemName = _ => "Dune: Part One",
            MediaTypes = () => ["Movies"],
        };
        list.Pending.SetField(MovieId, "title", "Dune 2");
        return list;
    }

    private void Show(IReviewSource source)
    {
        _ = ReviewChangesDialog.ShowAsync(Services.GetRequiredService<IAppDialogService>(), source);
        _dialogs.WaitForElement(".review-changes");
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes__state")));
    }

    private string FilesControlMarkup()
    {
        _dialogs.WaitForAssertion(() => Assert.NotEmpty(_dialogs.FindAll(".review-changes__files")));
        var markup = string.Concat(_dialogs.FindAll(".review-changes__files-block, .review-changes__files-override").Select(element => element.OuterHtml));

        // Blazor numbers each event handler and generated id afresh per render; only the look matters here.
        return Regex.Replace(markup, "(blazor:[a-z]+|id|for|aria-labelledby)=\"[^\"]*\"", string.Empty);
    }

    [Fact]
    public async Task TheControl_LooksTheSame_FromADetailPageAndFromAList()
    {
        Show(await DetailPageSourceAsync());
        var fromDetailPage = FilesControlMarkup();
        _dialogs.Find(".app-dialog-shell__footer button").Click();
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));

        Show(ListSource());
        var fromList = FilesControlMarkup();

        Assert.Contains("Files will be updated: <strong>On</strong>", fromDetailPage, StringComparison.Ordinal);
        Assert.Equal(fromDetailPage, fromList);
    }

    [Fact]
    public async Task TheLine_LinksToTheLibrarySettings_AndOffersNoOverride_WhenTheLibraryKeepsFilesAsTheyAre()
    {
        _libraryWritesTags = false;

        Show(ListSource());

        Assert.Equal("Files will be updated: Off", _dialogs.Find(".review-changes__files").TextContent.Trim());
        Assert.Equal(FileWriteSetting.SettingsHref, _dialogs.Find(".review-changes__files-link").GetAttribute("href"));
        Assert.Empty(_dialogs.FindAll(".review-changes__files-override"));
    }

    [Fact]
    public async Task TheLine_ReadsOff_WhenTheEngineWideSwitchIsOff_EvenIfTheLibraryWouldWrite()
    {
        _masterSwitch = false;

        Show(await DetailPageSourceAsync());

        Assert.Equal("Files will be updated: Off", _dialogs.Find(".review-changes__files").TextContent.Trim());
        Assert.Empty(_dialogs.FindAll(".review-changes__files-override"));
    }

    [Fact]
    public async Task TheOverride_SkipsTheFilesForThisSaveOnly_AndTheSaveCarriesIt()
    {
        var source = ListSource();
        Show(source);

        _dialogs.Find(".review-changes__files-override input").Change(false);
        Assert.False(source.WriteIntoFiles);
        _dialogs.Find(".app-dialog-shell__footer button:last-child").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        var save = Assert.Single(_saves, request => !request.PreviewOnly);
        Assert.False(save.WriteIntoFiles);
        Assert.Null(source.WriteIntoFiles);
    }

    [Fact]
    public async Task ASaveWithoutTouchingTheOverride_FollowsTheLibrary()
    {
        var source = ListSource();
        Show(source);

        _dialogs.Find(".app-dialog-shell__footer button:last-child").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.Null(Assert.Single(_saves, request => !request.PreviewOnly).WriteIntoFiles);
    }

    [Fact]
    public async Task TheLine_IsLeftOut_WhenTheSettingCannotBeRead()
    {
        _masterSwitch = null;

        Show(ListSource());

        Assert.Empty(_dialogs.FindAll(".review-changes__files"));
        Assert.Empty(_dialogs.FindAll(".review-changes__files-override"));
    }

    [Theory]
    [InlineData(true, true, FileWriteState.On)]
    [InlineData(true, false, FileWriteState.Off)]
    [InlineData(false, true, FileWriteState.Off)]
    [InlineData(false, false, FileWriteState.Off)]
    public void Resolver_FollowsTheLibraryUnderTheEngineWideSwitch(bool master, bool writeTags, FileWriteState expected)
    {
        var setting = FileWriteSettingResolver.Resolve(LibrariesWith("Music", writeTags), new FileWriteScheduleDto { Enabled = master }, ["music"]);

        Assert.Equal(expected, setting.State);
        Assert.Equal(expected == FileWriteState.On, setting.CanOverride);
    }

    [Fact]
    public void Resolver_SaysNothing_WhenNoLibraryHoldsTheType()
    {
        var setting = FileWriteSettingResolver.Resolve(LibrariesWith("Music", true), new FileWriteScheduleDto { Enabled = true }, ["Comics"]);

        Assert.Equal(FileWriteState.Unknown, setting.State);
        Assert.False(setting.CanOverride);
    }

    [Fact]
    public void Resolver_SaysNothing_WhenTheEngineDidNotAnswer()
    {
        Assert.Equal(FileWriteState.Unknown, FileWriteSettingResolver.Resolve(LibrariesWith("Music", true), null, ["Music"]).State);
    }

    private static LibrariesConfigurationDto LibrariesWith(string mediaType, bool writeTags) => new()
    {
        Libraries =
        [
            new LibraryFolderDto
            {
                Id = "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
                MediaTypes = [mediaType],
                OrganizationPolicy = new LibraryOrganizationPolicyDto { WriteTags = writeTags },
            },
        ],
    };
}
