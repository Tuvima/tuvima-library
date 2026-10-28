using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Settings;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// Adding files by dropping them on a library page or choosing them (Library setup D44, WP12b). The browser side is a recorded stand-in
// (the real one is covered by import-upload.test.mjs): the page is told about files and outcomes exactly as the browser would tell it.
public sealed class LibraryUploadZoneTests : AsyncBunitContext
{
    private const string ModulePath = "./js/import-upload.js";
    private const string MoviesId = "11111111-1111-4111-8111-111111111111";

    private readonly LibrariesConfigurationDto _configuration = new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MoviesId, Name = "Movies", KindKey = "movies", Area = "watch", Kind = "catalogued", MediaTypes = ["Movies"],
                Sources = [new() { Id = "movies-main", Path = "E:\\Library\\Movies", ManagementMode = "managed_by_tuvima", AccessMode = "writable" }],
            },
        ],
    };

    private readonly BunitJSModuleInterop _module;
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public LibraryUploadZoneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _module = JSInterop.SetupModule(ModulePath);
        _module.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProviderCatalogueAsync), _ => Task.FromResult<IReadOnlyList<ProviderCatalogueDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(_configuration));
            stub.SetHandler(nameof(IEngineApiClient.GetFolderJobsAsync), _ => Task.FromResult<IReadOnlyList<FolderJobStatusDto>>([]));
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        _dialogs = Render<AppDialogHost>();
    }

    private ImportUploadState State => Services.GetRequiredService<ImportUploadState>();

    // The browser telling the session's upload state how a file went, as the real module does.
    private async Task Finish(IRenderedComponent<LibraryUploadZone> cut, string id, UploadOutcome outcome) =>
        await cut.InvokeAsync(() => State.OnUploadFinished(id, outcome));

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private static UploadFileItem File(string id, string name, long size = 5_000_000, string type = "") =>
        new() { Id = id, Name = name, Size = size, Type = type };

    private LibraryTypeOption Kind(string key) => LibraryKindsTestData.Catalog.Find(key)!;

    // The wrapped list stands in for the page's own list.
    private IRenderedComponent<LibraryUploadZone> RenderZone(LibraryTypeOption? type = null, LibraryGroup? group = null, bool readOnly = false, bool embedded = false) =>
        Render<LibraryUploadZone>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.Configuration, _configuration)
            .Add(x => x.Type, type)
            .Add(x => x.Group, group)
            .Add(x => x.ReadOnly, readOnly)
            .Add(x => x.Embedded, embedded)
            .Add(x => x.ChildContent, (RenderFragment)(builder => builder.AddMarkupContent(0, "<div class=\"page-list\">the page's list</div><button type=\"button\">Add folder</button>"))));

    private IReadOnlyList<(string Kind, string? Library, object? Token)> Begins() =>
        [.. _module.Invocations["begin"].Select(call => ((string)call.Arguments[1]!, (string?)call.Arguments[2], call.Arguments[3]))];

    private Task Choose(IRenderedComponent<LibraryUploadZone> cut, params UploadFileItem[] items) =>
        cut.InvokeAsync(() => cut.Instance.OnFilesChosen(items, 0));

    // 1. With no Import folder a start is refused: the page says so with a link, and nothing but the start was attempted.
    [Fact]
    public async Task WithNoImportFolder_ADropIsRefusedWithTheMessageAndALink_AndNothingElseIsAsked()
    {
        var cut = RenderZone(Kind("music"));
        await Choose(cut, File("f1", "song.mp3"));
        Assert.Equal([("music", (string?)null)], Begins().Select(call => (call.Kind, call.Library)));

        await Finish(cut, "f1", new UploadOutcome { State = "refused", Code = "no_import_folder", Message = "Set an Import folder first." });

        var row = cut.Find(".upload-row");
        Assert.Contains("Set an Import folder first.", Text(row));
        Assert.Equal("/settings/libraries/import", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal("refused", row.GetAttribute("data-state"));
        Assert.Empty(_module.Invocations["cancel"]);
        Assert.Single(_module.Invocations["begin"]);
    }

    [Theory]
    [InlineData("not_enough_space", "There isn't enough room in the Import folder for this file.", "isn't enough room")]
    [InlineData("drag_and_drop_off", "An administrator turned off adding files by dropping them here.", "turned off")]
    [InlineData("vault_not_available", "whatever the Engine said", "Adding files to the Vault by drag and drop isn't available yet.")]
    public async Task EachRefusalReasonHasItsOwnPlainMessage(string code, string engineSaid, string expected)
    {
        var cut = RenderZone(Kind("movies"));
        await Choose(cut, File("f1", "film.mkv"));

        await Finish(cut, "f1", new UploadOutcome { State = "refused", Code = code, Message = engineSaid });

        Assert.Contains(expected, Text(cut.Find(".upload-row")));
        Assert.Single(_module.Invocations["begin"]);
    }

    [Fact]
    public async Task ARefusalForAKindWithNoFolder_LinksToThatKindsPage()
    {
        var cut = RenderZone(Kind("movies"));
        await Choose(cut, File("f1", "film.mkv"));

        await Finish(cut, "f1", new UploadOutcome { State = "refused", Code = "no_managed_destination", Message = "Movies has no folder Library manages yet." });

        Assert.Equal("/settings/libraries/watch/movies", cut.Find(".upload-row a").GetAttribute("href"));
    }

    // 2. A song dropped on Music uploads as music, shows its bar, and ends with the filed-on-next-run line (and the new name when renamed).
    [Fact]
    public async Task ASongDroppedOnMusic_UploadsAsMusic_ShowsProgress_AndSaysItWillBeFiledOnTheNextRun()
    {
        var cut = RenderZone(Kind("music"));

        await Choose(cut, File("f1", "song.mp3", 10_000_000));

        Assert.Equal("music", Begins().Single().Kind);
        Assert.Empty(_dialogs.FindAll(".upload-dialog"));
        await cut.InvokeAsync(() => State.OnUploadProgress("f1", 5_000_000, 10_000_000));
        var bar = cut.Find(".upload-row [role=progressbar]");
        Assert.Equal("50", bar.GetAttribute("aria-valuenow"));
        Assert.Contains("of", Text(cut.Find(".upload-row__detail")));
        Assert.NotNull(cut.Find(".upload-row button[aria-label='Cancel adding song.mp3']"));

        await Finish(cut, "f1", new UploadOutcome { State = "completed", FileName = "song (1).mp3", Renamed = true });

        var detail = Text(cut.Find(".upload-row__detail"));
        Assert.Contains("Added to the Import folder. It will be filed on the next run.", detail);
        Assert.Contains("Saved as song (1).mp3.", detail);
        Assert.Equal("done", cut.Find(".upload-row").GetAttribute("data-state"));
    }

    // 3. A film dropped on Listen: one dialog with Watch kind cards; the choice decides the kind. Several files share the one dialog.
    [Fact]
    public async Task FilmsDroppedOnListen_ShowOneLooksLikeVideosDialog_AndTheChoiceDecidesTheKind()
    {
        var cut = RenderZone(Kind("music"));

        var drop = Choose(cut, File("f1", "a.mkv", type: "video/x-matroska"), File("f2", "b.mp4", type: "video/mp4"));

        _dialogs.WaitForElement(".upload-dialog");
        Assert.Single(_dialogs.FindAll(".upload-dialog__section"));
        Assert.Contains("These look like videos, not music", Text(_dialogs.Find(".upload-dialog__section h3")));
        var cards = _dialogs.FindAll(".add-wizard__kind-option").Select(Text).ToList();
        Assert.Contains("Movies", cards);
        Assert.Contains("TV Shows", cards);
        Assert.DoesNotContain("Music", cards);
        // The usual kind card: tile, Metadata icons and the (i).
        Assert.NotEmpty(_dialogs.FindAll(".add-wizard__kind .lib-meta"));
        Assert.NotEmpty(_dialogs.FindAll(".add-wizard__info"));
        Assert.Contains("a.mkv", Text(_dialogs.Find(".upload-dialog__section .add-wizard__lead")));

        _dialogs.Find(".add-wizard__kind-option[data-type=movies]").Click();
        _dialogs.FindAll("button").Single(button => Text(button) == "Add files").Click();
        await drop;

        Assert.Equal(["movies", "movies"], Begins().Select(call => call.Kind));
        Assert.Equal(2, cut.FindAll(".upload-row").Count);
    }

    [Fact]
    public async Task ACancelledKindDialog_SendsNothing_AndLetsTheFilesGo()
    {
        var cut = RenderZone(Kind("music"));

        var drop = Choose(cut, File("f1", "a.mkv", type: "video/x-matroska"));
        _dialogs.WaitForElement(".upload-dialog");
        _dialogs.FindAll("button").Single(button => Text(button) == "Cancel").Click();
        await drop;

        Assert.Empty(_module.Invocations["begin"]);
        Assert.Empty(cut.FindAll(".upload-row"));
        Assert.Equal("f1", _module.Invocations["forget"].Single().Arguments[0]);
    }

    [Fact]
    public async Task OnAGroupPage_APersonIsAskedWhichKind_WithTheMostLikelyOneChosen()
    {
        var cut = RenderZone(group: LibraryKindsTestData.Catalog.TryGetGroup("watch"));

        var drop = Choose(cut, File("f1", "film.mkv"));

        _dialogs.WaitForElement(".upload-dialog");
        Assert.Contains("Which kind of videos are these?", Text(_dialogs.Find(".upload-dialog__section h3")));
        Assert.Contains("is-selected", _dialogs.Find(".add-wizard__kind-option[data-type=movies]").ClassName);
        _dialogs.FindAll("button").Single(button => Text(button) == "Add files").Click();
        await drop;
        Assert.Equal("movies", Begins().Single().Kind);
    }

    [Fact]
    public async Task APlainKindAsksWhichLibrary_AndNewCustomLibraryOpensAddFolderInsteadOfSending()
    {
        var cut = RenderZone(group: LibraryKindsTestData.Catalog.TryGetGroup("watch"));

        var drop = Choose(cut, File("f1", "clip.mp4"));
        _dialogs.WaitForElement(".upload-dialog");
        _dialogs.Find(".add-wizard__kind-option[data-type=other_videos]").Click();

        _dialogs.WaitForElement("[data-library=new]");
        Assert.NotNull(_dialogs.Find("#upload-dialog-0-library"));
        _dialogs.Find("[data-library=new]").Click();
        Assert.NotNull(_dialogs.Find("[data-new-library-note]"));
        _dialogs.FindAll("button").Single(button => Text(button) == "Set up the library").Click();
        await drop;

        Assert.Empty(_module.Invocations["begin"]);
        _dialogs.WaitForElement(".add-wizard");
    }

    // 4. A photo dropped on a Watch page is pointed at View and never uploaded here.
    [Fact]
    public async Task APhotoDroppedOnWatch_IsSentToViewWithALink_NotUploadedHere()
    {
        var cut = RenderZone(group: LibraryKindsTestData.Catalog.TryGetGroup("watch"));

        await Choose(cut, File("f1", "holiday.jpg", type: "image/jpeg"));

        var row = cut.Find(".upload-row");
        Assert.Contains("Photos belong in View", Text(row));
        Assert.Equal("/view", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.Empty(_module.Invocations["begin"]);
        Assert.Empty(_dialogs.FindAll(".upload-dialog"));
    }

    [Fact]
    public async Task AFileLibraryCannotFile_IsToldSo_AndAFolderIsLeftOut()
    {
        var cut = RenderZone(Kind("movies"));

        await cut.InvokeAsync(() => cut.Instance.OnFilesChosen([File("f1", "notes.txt")], 1));

        Assert.Equal(2, cut.FindAll(".upload-row").Count);
        Assert.Contains("Folders can't be dropped here", cut.Markup);
        Assert.Contains("can't file this kind of file", cut.Markup);
        Assert.Empty(_module.Invocations["begin"]);
    }

    // 5. Cancel asks the browser to delete the partial file, and the row goes.
    [Fact]
    public async Task Cancel_AsksForThePartialFileToBeDeleted_AndRemovesTheRow()
    {
        var cut = RenderZone(Kind("movies"));
        await Choose(cut, File("f1", "film.mkv"));
        await cut.InvokeAsync(() => State.OnUploadProgress("f1", 1_000_000, 5_000_000));

        cut.Find(".upload-row button[aria-label='Cancel adding film.mkv']").Click();

        Assert.Empty(cut.FindAll(".upload-row"));
        var call = Assert.Single(_module.Invocations["cancel"]);
        Assert.Equal("f1", call.Arguments[0]);
    }

    [Fact]
    public async Task AFailedUpload_OffersTryAgain_ThatCarriesOnWithoutAskingForTheKindAgain()
    {
        var cut = RenderZone(Kind("movies"));
        await Choose(cut, File("f1", "film.mkv"));
        await Finish(cut, "f1", new UploadOutcome { State = "failed", Message = "The connection to the Library kept dropping." });

        cut.Find(".upload-row button[aria-label='Try adding film.mkv again']").Click();

        Assert.Equal(2, _module.Invocations["begin"].Count);
        Assert.Equal("starting", cut.Find(".upload-row").GetAttribute("data-state"));
    }

    [Fact]
    public async Task AFileTheBrowserWasAlreadyPartwayThrough_CarriesOnAsTheKindItWasStartedFor_WithoutAskingAgain()
    {
        var cut = RenderZone(group: LibraryKindsTestData.Catalog.TryGetGroup("watch"));

        await cut.InvokeAsync(() => cut.Instance.OnFilesChosen([new UploadFileItem { Id = "f1", Name = "film.mkv", Size = 9, ResumeKind = "tv" }], 0));

        Assert.Equal("tv", Begins().Single().Kind);
        Assert.Empty(_dialogs.FindAll(".upload-dialog"));
        Assert.Contains("Carrying on where it stopped", Text(cut.Find(".upload-row")));
    }

    // 8. Touch devices see the button only; there is no drop area in first-run setup, on a phone, or on the Vault.
    [Fact]
    public void WithoutAFinePointer_ThereIsTheAddFilesButtonButNoDropHint()
    {
        var cut = Render<LibraryGroupPage>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.Group, LibraryKindsTestData.Catalog.TryGetGroup("listen")!)
            .Add(x => x.Configuration, _configuration));

        cut.WaitForAssertion(() => Assert.NotEmpty(_module.Invocations["attach"]));
        Assert.Single(cut.FindAll("button"), button => Text(button) == "Add files");
        Assert.Empty(cut.FindAll(".upload-zone__hint"));
        Assert.Equal(false, _module.Invocations["attach"].Single().Arguments[2]);
    }

    [Fact]
    public async Task WithAFinePointerThatCanHover_TheGroupPageHasADropHint_ThatOpensTheChooser()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var cut = Render<LibraryGroupPage>(parameters => parameters
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.Group, LibraryKindsTestData.Catalog.TryGetGroup("listen")!)
            .Add(x => x.Configuration, _configuration));

        cut.WaitForElement(".upload-zone__hint");

        Assert.Equal("Drop files here or choose Add files.", Text(cut.Find(".upload-zone__hint")));
        Assert.Equal(true, _module.Invocations["attach"].Single().Arguments[2]);
        cut.Find(".upload-zone__hint").Click();
        Assert.Single(_module.Invocations["choose"]);
        cut.FindAll("button").Single(button => Text(button) == "Add files").Click();
        Assert.Equal(2, _module.Invocations["choose"].Count);
    }

    [Fact]
    public void ThereIsNoDropAreaOrAddFilesInSetup_OnAPhone_OnTheVault_OrOnView()
    {
        var setup = RenderZone(Kind("movies"), embedded: true);
        var phone = RenderZone(Kind("movies"), readOnly: true);
        var vault = RenderZone(group: LibraryKindsTestData.Catalog.TryGetGroup("vault"));
        var view = RenderZone(group: LibraryKindsTestData.Catalog.TryGetGroup("view"));

        Assert.All(new[] { setup, phone, vault, view }, cut =>
        {
            Assert.Empty(cut.FindAll("[data-upload-anchor]"));
            Assert.Contains("the page's list", cut.Markup);
        });
        Assert.Empty(_module.Invocations["attach"]);
    }

    [Fact]
    public void TheTypePageOffersAddFilesBesideAddFolder()
    {
        var typePage = Render<LibrariesTab>(parameters => parameters.Add(x => x.Subsection, MoviesId));
        typePage.WaitForElement(".libpage__actions");

        Assert.Contains(typePage.FindAll(".libpage__actions button"), button => Text(button) == "Add files");
        Assert.Contains(typePage.FindAll(".libpage__actions button"), button => Text(button) == "Add folder");
    }

    private static UploadOutcome Large(string path = "E:\\Import\\Movies") => new()
    {
        State = "refused",
        Code = "large_upload_needs_confirm",
        Message = "Large uploads can fail.",
        ImportFolderPath = path,
    };

    // 7. A file above the warning size shows one amber box per drop (Library setup D44); nothing is sent until the person agrees.
    [Fact]
    public async Task LargeFilesOfOneDrop_ShareOneWarningBox_WithThePathAndCopyPath_AndNothingIsListedAsFailed()
    {
        var cut = RenderZone(Kind("movies"));
        await Choose(cut, File("f1", "big-1.mkv", 60_000_000_000), File("f2", "big-2.mkv", 70_000_000_000), File("f3", "small.mkv", 5_000_000));

        await Finish(cut, "f1", Large());
        await Finish(cut, "f2", Large());

        var box = Assert.Single(cut.FindAll("[data-upload-warning]"));
        Assert.Contains("Large uploads can fail. For files this size, copying straight into the Import folder on the server is more reliable.", Text(box));
        Assert.Contains("big-1.mkv", Text(box));
        Assert.Contains("big-2.mkv", Text(box));
        Assert.DoesNotContain("small.mkv", Text(box));
        Assert.Equal("E:\\Import\\Movies", Text(box.QuerySelector("code")!));
        Assert.Single(box.QuerySelectorAll("code"));
        // The small file is an ordinary row; the large ones are not listed as failed or refused.
        Assert.Equal(["small.mkv"], cut.FindAll(".upload-row").Select(row => row.GetAttribute("data-file")));

        box.QuerySelectorAll("button").Single(button => Text(button) == "Copy path").Click();
        JSInterop.VerifyInvoke("navigator.clipboard.writeText");
        Assert.Equal("E:\\Import\\Movies", JSInterop.Invocations["navigator.clipboard.writeText"].Single().Arguments[0]);
        Assert.Equal(3, _module.Invocations["begin"].Count);
    }

    [Fact]
    public async Task UploadAnyway_SendsTheLargeFilesAgainWithConfirmation_AndTheBoxGoes()
    {
        var cut = RenderZone(Kind("movies"));
        await Choose(cut, File("f1", "big-1.mkv", 60_000_000_000), File("f2", "big-2.mkv", 70_000_000_000));
        await Finish(cut, "f1", Large());
        await Finish(cut, "f2", Large());
        Assert.All(_module.Invocations["begin"], call => Assert.Equal(false, call.Arguments[5]));

        cut.FindAll("[data-upload-warning] button").Single(button => Text(button) == "Upload anyway").Click();

        Assert.Empty(cut.FindAll("[data-upload-warning]"));
        var again = _module.Invocations["begin"].Skip(2).ToList();
        Assert.Equal(["f1", "f2"], again.Select(call => (string)call.Arguments[0]!));
        Assert.All(again, call => Assert.Equal(true, call.Arguments[5]));
        Assert.Equal(2, cut.FindAll(".upload-row").Count);
    }

    [Fact]
    public async Task CancelOnTheWarning_DropsJustThoseFiles_AndTheOthersCarryOn()
    {
        var cut = RenderZone(Kind("movies"));
        await Choose(cut, File("f1", "big.mkv", 60_000_000_000), File("f2", "small.mkv", 5_000_000));
        await Finish(cut, "f1", Large());

        cut.FindAll("[data-upload-warning] button").Single(button => Text(button) == "Cancel").Click();

        Assert.Empty(cut.FindAll("[data-upload-warning]"));
        Assert.Equal(["small.mkv"], cut.FindAll(".upload-row").Select(row => row.GetAttribute("data-file")));
        Assert.Equal("f1", Assert.Single(_module.Invocations["forget"]).Arguments[0]);
        Assert.Empty(_module.Invocations["cancel"]);
    }

    // Uploads keep going while the person moves around: the list lives in the session's upload state, not in the page.
    [Fact]
    public async Task TheProgressList_ReappearsOnAnotherLibrariesPage_AndLeavingAPageDoesNotStopTheUpload()
    {
        var first = RenderZone(Kind("movies"));
        await Choose(first, File("f1", "film.mkv", 10_000_000));
        await first.InvokeAsync(() => State.OnUploadProgress("f1", 4_000_000, 10_000_000));

        await first.Instance.DisposeAsync();
        var second = RenderZone(group: LibraryKindsTestData.Catalog.TryGetGroup("watch"));

        Assert.Equal("film.mkv", second.Find(".upload-row").GetAttribute("data-file"));
        Assert.Equal("40", second.Find(".upload-row [role=progressbar]").GetAttribute("aria-valuenow"));
        Assert.Empty(_module.Invocations["cancel"]);
        // A result that arrives while the other page is shown lands in the same list.
        await Finish(second, "f1", new UploadOutcome { State = "completed", FileName = "film.mkv" });
        Assert.Equal("done", second.Find(".upload-row").GetAttribute("data-state"));
    }

    [Fact]
    public async Task ADeviceReportedLate_ShowsTheDropHint_AndListensForDrops()
    {
        var cut = RenderZone(Kind("movies"));
        cut.WaitForAssertion(() => Assert.Single(_module.Invocations["attach"]));
        Assert.Empty(cut.FindAll(".upload-zone__hint"));

        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

        cut.WaitForElement(".upload-zone__hint");
        cut.WaitForAssertion(() => Assert.Equal(true, _module.Invocations["attach"].Last().Arguments[2]));
        Assert.NotEmpty(_module.Invocations["detach"]);
    }
}
