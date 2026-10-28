using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// "Change what this folder is" (Library setup D40(c), D60): the row menu and paused-line entries, and the two-step dialog behind them.
public sealed class ChangeFolderKindTests : AsyncBunitContext
{
    private const string MusicId = "44444444-4444-4444-8444-444444444444";
    private const string LecturesId = "55555555-5555-4555-8555-555555555555";
    private const string AudiobooksId = "66666666-6666-4666-8666-666666666666";

    private readonly LibraryKindCatalog _kinds = new(LibraryKindsTestData.All());
    private LibrariesConfigurationDto _configuration = BuildConfiguration();
    private readonly List<(string SourceId, FolderKindChangeRequest Request)> _previews = [];
    private readonly List<(string SourceId, FolderKindChangeRequest Request)> _starts = [];
    private readonly List<string> _order = [];
    private FolderKindChangeResult<FolderKindChangePreviewResponse> _previewAnswer = new(Preview());
    private FolderKindChangeResult<FolderJobStatusDto>? _startAnswer;
    private string? _refuseCreate;
    private string? _lastError;
    private IRenderedComponent<AppDialogHost> _dialogs = null!;

    private static LibrariesConfigurationDto BuildConfiguration() => new()
    {
        StorageLocations = [new() { Id = "media", Label = "Media storage", Path = "C:\\media", AllowWrite = true }],
        Libraries =
        [
            new()
            {
                Id = MusicId, Name = "Music", KindKey = "music", Area = "listen", Kind = "catalogued", MediaTypes = ["Music"],
                PrimaryDestinationSourceId = "music-main",
                Sources =
                [
                    new() { Id = "music-main", Path = "E:\\Library\\Music", Notes = "Library Music", ManagementMode = "managed_by_tuvima", AccessMode = "writable" },
                    new() { Id = "music-old", Path = "F:\\Music", Notes = "Old drive", ManagementMode = "existing_library", AccessMode = "read_only" },
                ],
            },
            new()
            {
                Id = AudiobooksId, Name = "Audiobooks", KindKey = "audiobooks", Area = "listen", Kind = "catalogued", MediaTypes = ["Audiobooks"],
                Sources = [new() { Id = "audiobooks-1", Path = "C:\\media\\Audiobooks", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
            new()
            {
                Id = LecturesId, Name = "Lectures", KindKey = "other_audio", Area = "listen", Kind = "catalogued", MediaTypes = ["Music"],
                Sources = [new() { Id = "lectures-1", Path = "C:\\media\\Lectures", ManagementMode = "existing_library", AccessMode = "read_only" }],
            },
        ],
    };

    private static FolderKindChangePreviewResponse Preview(Action<FolderKindChangePreviewResponse>? adjust = null)
    {
        var preview = new FolderKindChangePreviewResponse
        {
            SourceId = "music-old",
            FromKindLabel = "Music",
            ToKindKey = "audiobooks",
            ToKindLabel = "Audiobooks",
            ToLibraryId = "audiobooks-id",
            ToLibraryName = "Audiobooks",
            FileCount = 40,
            FilesToRefile = 40,
            DropsDetails = true,
            StartsFirstCheck = true,
            OtherAppsWarning = true,
            VisibilityLossChecked = true,
            VisibilityLoss = [new VisibilityLossDto(Guid.NewGuid(), "Sam", 40)],
        };
        adjust?.Invoke(preview);
        return preview;
    }

    public ChangeFolderKindTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => _lastError);
            stub.SetHandler(nameof(IEngineApiClient.GetProviderCatalogueAsync), _ => Task.FromResult<IReadOnlyList<ProviderCatalogueDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                _order.Add("create");
                if (_refuseCreate is not null)
                {
                    _lastError = _refuseCreate;
                    return Task.FromResult<LibrariesConfigurationDto?>(null);
                }

                var library = ((LibraryMutationRequest)args![0]!).Library!;
                _configuration = new LibrariesConfigurationDto
                {
                    StorageLocations = _configuration.StorageLocations,
                    Libraries = [.. _configuration.Libraries.Where(item => item.Id != library.Id), library],
                };
                return Task.FromResult<LibrariesConfigurationDto?>(_configuration);
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewFolderKindChangeAsync), args =>
            {
                _order.Add("preview");
                _previews.Add(((string)args![0]!, (FolderKindChangeRequest)args[1]!));
                return Task.FromResult(_previewAnswer);
            });
            stub.SetHandler(nameof(IEngineApiClient.ChangeFolderKindAsync), args =>
            {
                _order.Add("start");
                _starts.Add(((string)args![0]!, (FolderKindChangeRequest)args[1]!));
                return Task.FromResult(_startAnswer ?? new FolderKindChangeResult<FolderJobStatusDto>(
                    new FolderJobStatusDto { OperationId = "change-1", SourceId = (string)args[0]!, State = "queued" }));
            });
        }));
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddScoped<MediaEngine.Web.Services.Theming.DeviceContextService>();
        _dialogs = Render<AppDialogHost>();
    }

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private Task<FolderFollowUp> OpenDialog(string libraryId = MusicId, string sourceId = "music-old")
    {
        var library = _configuration.Libraries.Single(item => item.Id == libraryId);
        var source = library.Sources.Single(item => item.Id == sourceId);
        var task = Services.GetRequiredService<LibraryFolderAddService>().ChangeKindAsync(FolderAddScope.Settings, _kinds, _configuration, library, source);
        _dialogs.WaitForElement(".lib-choice__kinds, [data-no-kinds]");
        return task;
    }

    private AngleSharp.Dom.IElement Button(string text) => _dialogs.FindAll("button").Single(x => Text(x) == text);

    private void Click(string text) => Button(text).Click();

    private void ChooseKind(string key) => _dialogs.Find($".lib-choice__kind-option[data-type={key}]").Click();

    private IEnumerable<string> Lines => _dialogs.FindAll("[data-impact]").Select(Text);

    // Step 1 to the impact for a kind that holds one library.
    private void GoToImpact(string kind = "audiobooks")
    {
        ChooseKind(kind);
        Click("Next");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.FindAll("[data-impact], [data-refusal]").Count > 0));
    }

    // 1. The row menu.
    private IRenderedComponent<LibraryFolderTable> RenderTable(
        FolderJobStatusDto? job = null, bool offered = true, bool readOnly = false, string area = "listen", Action<LibrarySourceDto>? adjust = null,
        List<LibrarySourceDto>? asked = null)
    {
        var library = BuildConfiguration().Libraries[0];
        library.Area = area;
        if (adjust is not null)
        {
            library.Sources.ForEach(adjust);
        }

        Render<AppPopoverHost>();
        return Render<LibraryFolderTable>(parameters =>
        {
            parameters.Add(x => x.Library, library)
                .Add(x => x.TypeLabel, "Music")
                .Add(x => x.PluralLabel, "music")
                .Add(x => x.ReadOnly, readOnly)
                .Add(x => x.OnKeepGoing, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<LibrarySourceDto>(this, _ => { }));
            if (offered)
            {
                parameters.Add(x => x.OnChangeKind, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<LibrarySourceDto>(this, source => asked?.Add(source)));
            }

            if (job is not null)
            {
                parameters.Add(x => x.Jobs, new Dictionary<string, FolderJobStatusDto> { [job.SourceId] = job });
            }
        });
    }

    private static IEnumerable<string> MenuLabels(IRenderedComponent<LibraryFolderTable> cut, int row)
    {
        cut.FindAll(".folder-table__row")[row].QuerySelector(".folder-table__menu-trigger")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=menuitem]")));
        return cut.FindAll("[role=menuitem]").Select(Text).ToList();
    }

    [Fact]
    public void TheRowMenuOffersChangeWhatThisFolderIsAfterMergeIntoForAManagedAndAReadOnlyFolder()
    {
        var cut = RenderTable();

        // The read-only folder can be merged into the managed one, so Merge into is there and Change follows it.
        var readOnly = MenuLabels(cut, 1).ToList();
        Assert.Contains("Merge into…", readOnly);
        Assert.Equal(readOnly.IndexOf("Merge into…") + 1, readOnly.IndexOf("Change what this folder is…"));
        Assert.Contains("Change what this folder is…", MenuLabels(RenderTable(), 0));
    }

    [Fact]
    public void TheRowMenuHidesChangeWhileAJobRunsOnTheFolder()
    {
        var cut = RenderTable(job: new FolderJobStatusDto { OperationId = "job-1", SourceId = "music-old", State = "running", ItemsTotal = 10, ItemsCompleted = 2 });

        Assert.DoesNotContain("Change what this folder is…", MenuLabels(cut, 1));
        Assert.Contains("Change what this folder is…", MenuLabels(cut, 0));
    }

    [Fact]
    public void TheRowMenuHidesChangeForAVaultFolderInSetupAndOnPhones()
    {
        Assert.DoesNotContain("Change what this folder is…", MenuLabels(RenderTable(area: "vault"), 0));
        // First-run setup passes no handler, so there is nothing to offer.
        Assert.DoesNotContain("Change what this folder is…", MenuLabels(RenderTable(offered: false), 0));
        // A read-only page (phones) draws no row menu at all.
        Assert.Empty(RenderTable(readOnly: true).FindAll(".folder-table__menu-trigger"));
    }

    // 2. The paused first-check line.
    [Fact]
    public void ThePausedLineOffersChangeWhatThisFolderIsBesideKeepGoingAndRemoveTheFolder()
    {
        var asked = new List<LibrarySourceDto>();
        var cut = RenderTable(adjust: source =>
        {
            if (source.Id == "music-old")
            {
                source.FirstCheck = new() { State = "paused", Tried = 50, Unmatched = 30, TotalFiles = 300 };
            }
        }, asked: asked);

        var line = Assert.Single(cut.FindAll("[data-first-check=paused]"));
        Assert.Equal(["Keep going", "Remove the folder", "Change what this folder is"], line.QuerySelectorAll("button").Select(Text).ToArray());

        line.QuerySelectorAll("button").Single(button => Text(button) == "Change what this folder is").Click();
        Assert.Equal(["music-old"], asked.Select(source => source.Id));
    }

    // 3. Step 1: what is this folder?
    [Fact]
    public void StepOneOffersOnlyTheKindsOfTheFoldersOwnAreaWithoutItsCurrentKind()
    {
        _ = OpenDialog();

        Assert.Equal(["Audiobooks", "Other audio"], _dialogs.FindAll(".lib-choice__kind-option").Select(Text).ToArray());
        Assert.Equal(["audiobooks", "other_audio"], _dialogs.FindAll(".lib-choice__kind-option").Select(x => x.GetAttribute("data-type")!).ToArray());
        // The usual kind cards: the (i) and the Metadata icons come with them.
        Assert.Equal(2, _dialogs.FindAll(".lib-choice__info").Count);
        Assert.Equal(2, _dialogs.FindAll(".lib-meta").Count);
        Assert.DoesNotContain(_dialogs.FindAll("button"), x => Text(x) == "Change folder");
    }

    [Fact]
    public void AKindWithSeveralNamedLibrariesAsksWhichLibraryWithTheExistingOnesAndNewCustomLibrary()
    {
        _ = OpenDialog();
        Assert.Empty(_dialogs.FindAll("[data-library]"));

        ChooseKind("other_audio");

        Assert.Equal([LecturesId, "new"], _dialogs.FindAll("[data-library]").Select(x => x.GetAttribute("data-library")!).ToArray());
        Assert.Equal("true", _dialogs.Find($"[data-library='{LecturesId}']").GetAttribute("aria-checked"));
        Assert.Contains("Lectures", _dialogs.Markup);
        Assert.Contains("New custom library", _dialogs.Markup);
    }

    // 4. Step 2: the impact.
    [Fact]
    public void StepTwoStatesTheMoveTheRefilingTheDroppedDetailsTheFirstCheckTheOtherAppsAndWhoLosesSight()
    {
        _ = OpenDialog();

        GoToImpact();

        Assert.Equal(
        [
            "40 files will move to Audiobooks.",
            "Their files will be re-filed into Audiobooks's folder pattern in the next batch.",
            "Details Library looked up online for these files will be removed. They'll show as they are, by file name and tags. Your own edits, progress and ratings stay.",
            "Library will check the first files under Audiobooks first. Nothing is re-filed until that check passes.",
            "Other apps that point at these files (another player, a playlist, a backup) will lose them when they move.",
            "Sam won't see these 40 files.",
        ], Lines.ToArray());
        Assert.Equal("music-old", Assert.Single(_previews).SourceId);
        Assert.Equal("audiobooks", _previews[0].Request.KindKey);
        Assert.Contains(_dialogs.FindAll("button"), x => Text(x) == "Change folder");
        Assert.Empty(_starts);
    }

    [Fact]
    public void StepTwoLeavesOutTheLinesThatDoNotApplyAndNamesAtMostFivePeople()
    {
        _previewAnswer = new(Preview(p =>
        {
            p.FilesToRefile = 0;
            p.DropsDetails = false;
            p.StartsFirstCheck = false;
            p.OtherAppsWarning = false;
            p.VisibilityLoss = [.. Enumerable.Range(1, 7).Select(i => new VisibilityLossDto(Guid.NewGuid(), $"Person{i}", 3))];
        }));
        _ = OpenDialog();

        GoToImpact();

        Assert.Equal(
        [
            "40 files will move to Audiobooks.",
            "Person1 won't see these 3 files.",
            "Person2 won't see these 3 files.",
            "Person3 won't see these 3 files.",
            "Person4 won't see these 3 files.",
            "Person5 won't see these 3 files.",
            "and 2 others",
        ], Lines.ToArray());
    }

    [Fact]
    public void AFolderWithNoFilesSaysOnlyItsKindChangesAndLeavesOutTheMoveRefileAndDropLines()
    {
        _previewAnswer = new(Preview(p =>
        {
            p.FileCount = 0;
            p.FilesToRefile = 0;
            p.DropsDetails = true;
            p.StartsFirstCheck = false;
            p.OtherAppsWarning = false;
            p.VisibilityLoss = [];
        }));
        _ = OpenDialog();

        GoToImpact();

        Assert.Equal(["This folder has no files yet, so only its kind changes."], Lines.ToArray());
        Assert.Contains(_dialogs.FindAll("button"), x => Text(x) == "Change folder");
    }

    [Fact]
    public void ARefusalShowsItsSentenceWithNoChangeFolderButton()
    {
        _previewAnswer = new(null, Refusal: "This folder is being organised, merged or changed right now. Wait for that to finish first.");
        _ = OpenDialog();

        GoToImpact();

        Assert.Equal("This folder is being organised, merged or changed right now. Wait for that to finish first.", Text(_dialogs.Find("[data-refusal]")));
        Assert.DoesNotContain(_dialogs.FindAll("button"), x => Text(x) == "Change folder");
        Assert.Contains(_dialogs.FindAll("button"), x => Text(x) == "Close");
        Assert.Empty(_starts);
    }

    // 7. Who would lose sight could not be worked out.
    [Fact]
    public void WhenWhoWillStillSeeTheFilesWasNotCheckedItSaysSoAndTheChangeCanStillBeConfirmed()
    {
        _previewAnswer = new(Preview(p =>
        {
            p.VisibilityLossChecked = false;
            p.VisibilityLoss = [];
            p.VisibilityMessage = "Not worked out.";
        }));
        _ = OpenDialog();

        GoToImpact();

        Assert.Contains("Library couldn't check who will still see these files.", Lines);
        Assert.DoesNotContain(Lines, line => line.Contains("won't see", StringComparison.Ordinal));
        Assert.Contains(_dialogs.FindAll("button"), x => Text(x) == "Change folder");
    }

    // 5. Nothing is sent until Change folder.
    [Fact]
    public async Task ChangeFolderStartsTheChangeWithTheChosenKindAndClosesTheDialog()
    {
        var followUp = OpenDialog();
        GoToImpact();
        Assert.Empty(_starts);

        Click("Change folder");

        var result = await followUp;
        Assert.True(result.Started);
        var start = Assert.Single(_starts);
        Assert.Equal("music-old", start.SourceId);
        Assert.Equal("audiobooks", start.Request.KindKey);
        // A kind that already has its library always names it: a blank id would mean a new library.
        Assert.Equal(AudiobooksId, start.Request.LibraryId);
        Assert.Equal(AudiobooksId, Assert.Single(_previews).Request.LibraryId);
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".lib-choice__kinds")));
    }

    [Fact]
    public async Task ChangeFolderIntoAnExistingNamedLibraryStartsItWithThatLibrary()
    {
        var followUp = OpenDialog();
        ChooseKind("other_audio");
        Click("Next");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.FindAll("[data-impact]").Count > 0));

        Click("Change folder");

        await followUp;
        Assert.Equal(("music-old", "other_audio", LecturesId), (_starts[0].SourceId, _starts[0].Request.KindKey, _starts[0].Request.LibraryId));
        Assert.Equal(["preview", "start"], _order);
    }

    [Fact]
    public async Task CancelSendsNothing()
    {
        var followUp = OpenDialog();
        GoToImpact();

        Click("Cancel");

        var result = await followUp;
        Assert.False(result.Started);
        Assert.Empty(_starts);
        Assert.DoesNotContain("create", _order);
    }

    // 6. A library the kind does not have yet is made first.
    [Fact]
    public async Task NewCustomLibraryCreatesTheLibraryFirstThenStartsTheChangeWithItsId()
    {
        var followUp = OpenDialog();
        ChooseKind("other_audio");
        _dialogs.Find("[data-library=new]").Click();
        _dialogs.Find(".lib-choice__field input").Input("Family recordings");
        Click("Next");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.FindAll("[data-impact]").Count > 0));
        Click("Change folder");

        await followUp;
        var created = _configuration.Libraries.Single(library => library.Name == "Family recordings");
        Assert.Equal("other_audio", created.KindKey);
        Assert.Empty(created.Sources);
        Assert.Equal(["preview", "create", "start"], _order);
        Assert.Equal(created.Id, _starts[0].Request.LibraryId);
        Assert.Null(_previews[0].Request.LibraryId);
    }

    [Fact]
    public async Task AKindWithOneLibraryAndNoneYetGetsItsLibraryMadeFirst()
    {
        _configuration.Libraries.RemoveAll(library => library.Id == AudiobooksId);
        var followUp = OpenDialog();
        GoToImpact("audiobooks");
        Click("Change folder");

        await followUp;
        Assert.Equal(["preview", "create", "start"], _order);
        Assert.Null(_previews[0].Request.LibraryId);
        var created = _configuration.Libraries.Single(library => library.KindKey == "audiobooks");
        Assert.Equal("Audiobooks", created.Name);
        Assert.Equal(created.Id, _starts[0].Request.LibraryId);
    }

    [Fact]
    public async Task CancellingAfterTheImpactOfANewLibraryCreatesNothing()
    {
        var followUp = OpenDialog();
        ChooseKind("other_audio");
        _dialogs.Find("[data-library=new]").Click();
        _dialogs.Find(".lib-choice__field input").Input("Family recordings");
        Click("Next");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.FindAll("[data-impact]").Count > 0));

        Click("Cancel");

        var result = await followUp;
        Assert.False(result.Started);
        Assert.Equal(["preview"], _order);
        Assert.Null(_previews[0].Request.LibraryId);
        Assert.DoesNotContain(_configuration.Libraries, library => library.Name == "Family recordings");
    }

    [Fact]
    public async Task IfCreatingTheLibraryFailsTheDialogSaysSoAndNothingIsStarted()
    {
        _refuseCreate = "You already have a library with that name.";
        var followUp = OpenDialog();
        ChooseKind("other_audio");
        _dialogs.Find("[data-library=new]").Click();
        _dialogs.Find(".lib-choice__field input").Input("Lectures");
        Click("Next");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.FindAll("[data-impact]").Count > 0));

        Click("Change folder");

        _dialogs.WaitForAssertion(() => Assert.Contains("You already have a library with that name.", _dialogs.Markup));
        Assert.Empty(_starts);
        Click("Cancel");
        Assert.False((await followUp).Started);
    }

    [Fact]
    public async Task IfStartingFailsAfterTheLibraryWasCreatedItSaysTheLibraryWasCreatedButTheFolderWasNotChanged()
    {
        _startAnswer = new(null, Refusal: "This folder is being organised right now.");
        var followUp = OpenDialog();
        ChooseKind("other_audio");
        _dialogs.Find("[data-library=new]").Click();
        _dialogs.Find(".lib-choice__field input").Input("Family recordings");
        Click("Next");
        _dialogs.WaitForAssertion(() => Assert.True(_dialogs.FindAll("[data-impact]").Count > 0));

        Click("Change folder");

        _dialogs.WaitForAssertion(() => Assert.Contains("The Family recordings library was created, but the folder could not be changed: This folder is being organised right now.", _dialogs.Markup));
        Click("Cancel");
        Assert.True((await followUp).Started);
    }
}
