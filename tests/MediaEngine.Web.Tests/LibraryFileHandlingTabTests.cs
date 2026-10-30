using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// A library's File handling tab (Library pages WP5, D6): four cards, both switches off by default, the file count and the existing organising
// job only after Confirm, Discard, duplicates, Recycle bin, the custom pattern check and the phone view.
public sealed class LibraryFileHandlingTabTests : AsyncBunitContext
{
    private const string LibraryId = "11111111-1111-4111-8111-111111111111";

    private readonly List<LibraryMutationRequest> _mutations = [];
    private readonly List<string> _converted = [];
    private readonly List<string> _order = [];
    private string? _mutationError;
    private int _changed;
    private int _quickCount = 2140;
    private IRenderedComponent<AppDialogHost> _dialogs = null!;

    public LibraryFileHandlingTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => _mutationError);
            stub.SetHandler(nameof(IEngineApiClient.GetOrganizationTemplateAsync), _ => Task.FromResult<OrganizationTemplateDto?>(
                new OrganizationTemplateDto("{Category}/{Title}{Ext}", null, new() { ["Movies"] = "Movies/{Title} ({Year})/{Title} ({Year}){Ext}" })));
            stub.SetHandler(nameof(IEngineApiClient.PreviewOrganizationTemplateAsync), args =>
            {
                var template = (string)args![0]!;
                return Task.FromResult<OrganizationTemplateDto?>(template.Contains("{Bad", StringComparison.Ordinal)
                    ? null
                    : new OrganizationTemplateDto(template, template.StartsWith("Movies/", StringComparison.Ordinal) ? "Movies/Dune (2021)/Dune (2021).mkv" : "Dune (2021) - mine.mkv"));
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewFolderConversionAsync), _ => Task.FromResult<FolderConversionPreviewResponse?>(
                new() { TotalFiles = _quickCount, VideoFiles = _quickCount, EstimatedSeconds = 10700, DuplicatePolicy = "keep_one" }));
            stub.SetHandler(nameof(IEngineApiClient.MutateLibraryAsync), args =>
            {
                _order.Add("mutate");
                _mutations.Add((LibraryMutationRequest)args![0]!);
                return Task.FromResult<LibrariesConfigurationDto?>(_mutationError is null ? new LibrariesConfigurationDto() : null);
            });
            stub.SetHandler(nameof(IEngineApiClient.ConvertFolderAsync), args =>
            {
                _order.Add("convert");
                _converted.Add((string)args![0]!);
                return Task.FromResult<FolderJobStatusDto?>(new FolderJobStatusDto { OperationId = "job-1", SourceId = (string)args![0]!, State = "queued" });
            });
        }));
        _dialogs = Render<AppDialogHost>();
    }

    private static LibraryFolderDto Library(bool vault = false, string mode = "keep_original_names", bool managed = false, bool tags = false) => new()
    {
        Id = LibraryId,
        Name = "Movies",
        KindKey = "movies",
        Area = "watch",
        MediaTypes = ["Movies"],
        IsVault = vault,
        OrganizationPolicy = new() { Mode = mode, WriteTags = tags },
        Sources =
        [
            new()
            {
                Id = "movies-main",
                Path = "E:\\Movies",
                ManagementMode = managed ? "managed_by_tuvima" : "existing_library",
                AccessMode = managed ? "writable" : "read_only",
            },
        ],
    };

    private static string Text(AngleSharp.Dom.IElement element) => Regex.Replace(element.TextContent, "\\s+", " ").Trim();

    private LibraryFolderDto _library = Library();

    private IRenderedComponent<LibraryFileHandlingTab> Open(bool readOnly = false, string? serverDuplicate = "keep_one") =>
        Render<LibraryFileHandlingTab>(parameters => parameters
            .Add(x => x.Library, _library)
            .Add(x => x.Kinds, LibraryKindsTestData.Catalog)
            .Add(x => x.ServerDuplicatePolicy, serverDuplicate)
            .Add(x => x.ReadOnly, readOnly)
            .Add(x => x.Changed, EventCallback.Factory.Create(this, () => _changed++)));

    private static AngleSharp.Dom.IElement Switch(IRenderedComponent<LibraryFileHandlingTab> cut, string label) =>
        cut.FindAll(".app-switch-row").Single(row => row.QuerySelector(".app-switch-row__label")!.TextContent == label).QuerySelector("input[type=checkbox]")!;

    private static AngleSharp.Dom.IElement Choice(IRenderedComponent<LibraryFileHandlingTab> cut, string title) =>
        cut.FindAll(".app-choice-row").Single(button => button.QuerySelector("strong")!.TextContent == title);

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<LibraryFileHandlingTab> cut, string label) =>
        cut.FindAll("button").Single(button => Text(button) == label);

    private Task ClickDialogAsync(string label) =>
        _dialogs.ClickAsync(host => host.FindAll("button").Single(button => Text(button) == label));

    [Fact]
    public void ABrandNewLibraryShowsFourCardsWithOneInfoTipEach_PreserveNamesSelected_BothSwitchesOff()
    {
        var cut = Open();

        Assert.Equal(["Organization", "File changes", "Duplicates", "Deleted files"], cut.FindAll(".library-handling__card .app-card__title").Select(Text).ToArray());
        Assert.Equal(4, cut.FindAll(".library-handling__card .app-card__header .app-info-tip").Count);
        Assert.Equal(4, cut.FindAll(".library-handling__example").Count);
        Assert.True(Choice(cut, "Preserve original names").ClassList.Contains("is-selected"));
        Assert.False(Switch(cut, "Rename or move files automatically").HasAttribute("checked"));
        Assert.False(Switch(cut, "Write metadata into media files").HasAttribute("checked"));
        Assert.Empty(cut.FindAll(".app-save-bar"));
        Assert.Contains("Folders tab", cut.Find(".library-handling > p").TextContent, StringComparison.Ordinal);
        Assert.Contains("tab=folders", cut.Find(".library-handling > p a").GetAttribute("href"), StringComparison.Ordinal);
    }

    [Fact]
    public void TurningRenameOnShowsTheFileCountAndStartsNothingUntilConfirm()
    {
        var cut = Open();

        Switch(cut, "Rename or move files automatically").Change(true);

        cut.WaitForAssertion(() => Assert.Equal("2140", cut.Find("[data-pending-files]").GetAttribute("data-pending-files")));
        Assert.Contains("2,140 files will be renamed or moved", Text(cut.Find(".app-alert")), StringComparison.Ordinal);
        Assert.Contains("can't be undone automatically", Text(cut.Find(".app-alert")), StringComparison.Ordinal);
        Assert.Contains("You have unsaved changes", Text(cut.Find(".app-save-bar")), StringComparison.Ordinal);
        Assert.Empty(_mutations);
        Assert.Empty(_converted);
    }

    [Fact]
    public async Task SavingRenameOnAsksFirstAndStartsTheExistingJobOnlyAfterConfirm()
    {
        var cut = Open();
        Switch(cut, "Rename or move files automatically").Change(true);
        cut.WaitForElement("[data-pending-files]");

        Button(cut, "Save changes").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Let Library manage E:\\Movies?", _dialogs.Markup, StringComparison.Ordinal));
        Assert.Contains("This cannot be undone automatically", _dialogs.Markup, StringComparison.Ordinal);
        Assert.Empty(_mutations);
        Assert.Empty(_converted);

        await ClickDialogAsync("Convert");

        cut.WaitForAssertion(() => Assert.Equal(["movies-main"], _converted));
        Assert.Single(_mutations);
        Assert.Equal(["mutate", "convert"], _order);
        cut.WaitForAssertion(() => Assert.Equal(1, _changed));
    }

    [Fact]
    public async Task DecliningTheQuestionChangesNothing_AndTheEditsStay()
    {
        var cut = Open();
        Switch(cut, "Rename or move files automatically").Change(true);
        cut.WaitForElement("[data-pending-files]");

        Button(cut, "Save changes").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Keep read-only", _dialogs.Markup, StringComparison.Ordinal));
        await ClickDialogAsync("Keep read-only");

        cut.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".convert-folder")));
        Assert.Empty(_mutations);
        Assert.Empty(_converted);
        Assert.Equal(0, _changed);
        Assert.NotEmpty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void ALibraryWithNoFilesBecomesManagedInTheSaveWithoutAJob()
    {
        _quickCount = 0;
        var cut = Open();
        Switch(cut, "Rename or move files automatically").Change(true);
        cut.WaitForAssertion(() => Assert.Contains("no files to rename yet", cut.Markup, StringComparison.Ordinal));

        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("managed_by_tuvima", _mutations[0].Library!.Sources[0].ManagementMode);
        Assert.Equal("writable", _mutations[0].Library!.Sources[0].AccessMode);
        Assert.Empty(_converted);
    }

    [Fact]
    public async Task TurningRenameOffAsksThenUsesTheAsIsSwitch()
    {
        _library = Library(managed: true);
        var cut = Open();
        Assert.True(Switch(cut, "Rename or move files automatically").HasAttribute("checked"));

        Switch(cut, "Rename or move files automatically").Change(false);
        Button(cut, "Save changes").Click();
        _dialogs.WaitForAssertion(() => Assert.Contains("Stop renaming and moving files?", _dialogs.Markup, StringComparison.Ordinal));
        Assert.Empty(_mutations);
        await ClickDialogAsync("Stop renaming");

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("existing_library", _mutations[0].Library!.Sources[0].ManagementMode);
        Assert.Equal("read_only", _mutations[0].Library!.Sources[0].AccessMode);
        Assert.Empty(_converted);
    }

    [Fact]
    public void DiscardPutsEverySavedValueBackAndTheBarGoesAway()
    {
        var cut = Open();
        Switch(cut, "Rename or move files automatically").Change(true);
        Switch(cut, "Write metadata into media files").Change(true);
        Switch(cut, "Use Recycle bin").Change(true);
        Choice(cut, "Library standard").Click();
        cut.FindAll(".app-segmented button, [role=radiogroup] button, [role=group] button").Single(button => Text(button) == "Keep both").Click();

        Button(cut, "Discard").Click();

        Assert.False(Switch(cut, "Rename or move files automatically").HasAttribute("checked"));
        Assert.False(Switch(cut, "Write metadata into media files").HasAttribute("checked"));
        Assert.False(Switch(cut, "Use Recycle bin").HasAttribute("checked"));
        Assert.True(Choice(cut, "Preserve original names").ClassList.Contains("is-selected"));
        Assert.Contains("Using the server default (Keep one copy)", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".app-save-bar"));
        Assert.Empty(_mutations);
    }

    [Fact]
    public void DuplicatesRecycleBinAndMetadataAreSavedWithTheLibrary()
    {
        var cut = Open();
        cut.FindAll("[role=group] button, [role=radiogroup] button").Single(button => Text(button) == "Keep both").Click();
        Switch(cut, "Use Recycle bin").Change(true);
        Switch(cut, "Write metadata into media files").Change(true);

        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        var saved = _mutations[0].Library!;
        Assert.Equal("keep_both", saved.FileHandling.DuplicatePolicy);
        Assert.True(saved.RecycleBin);
        Assert.True(saved.OrganizationPolicy.WriteTags);
        Assert.Equal("keep_original_names", saved.OrganizationPolicy.Mode);
        Assert.Equal("keep_original_names", _mutations[0].ExpectedLibrary!.OrganizationPolicy.Mode);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void ALibraryWithNoDuplicateRuleSaysItUsesTheServerDefault_AndTheChoiceCanGoBackToIt()
    {
        _library = Library();
        _library.FileHandling.DuplicatePolicy = "keep_both";
        var cut = Open(serverDuplicate: "keep_one");
        Assert.DoesNotContain("Using the server default", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("[role=group] button, [role=radiogroup] button").Single(button => Text(button) == "Use server default").Click();
        Assert.Contains("Using the server default (Keep one copy)", cut.Markup, StringComparison.Ordinal);
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Null(_mutations[0].Library!.FileHandling.DuplicatePolicy);
    }

    [Fact]
    public void AnInvalidCustomPatternShowsTheEnginesErrorInlineAndCannotBeSaved()
    {
        var cut = Open();
        Choice(cut, "Custom pattern").Click();
        var field = cut.Find("input[type=text], input:not([type])");

        field.Input("{Bad}");

        cut.WaitForAssertion(() => Assert.Contains("That pattern isn't valid", cut.Markup, StringComparison.Ordinal));
        Assert.True(Button(cut, "Save changes").HasAttribute("disabled"));

        field.Input("Movies/{Title} ({Year})/{Title} ({Year}){Ext}");
        cut.WaitForAssertion(() => Assert.DoesNotContain("That pattern isn't valid", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Movies/Dune (2021)/Dune (2021).mkv", Text(cut.Find(".library-handling__card .library-handling__example")), StringComparison.Ordinal);
        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal("custom", _mutations[0].Library!.OrganizationPolicy.Mode);
        Assert.Equal("Movies/{Title} ({Year})/{Title} ({Year}){Ext}", _mutations[0].Library!.OrganizationPolicy.CustomTemplate);
    }

    [Fact]
    public void ALibraryStandardLibraryShowsTheStandardPathForItsKindInTheExampleLine()
    {
        _library = Library(mode: "tuvima_standard");
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Contains("Movies/Dune (2021)/Dune (2021).mkv", Text(cut.Find(".library-handling__example")), StringComparison.Ordinal));
        Assert.True(Choice(cut, "Library standard").ClassList.Contains("is-selected"));
    }

    [Fact]
    public void WhenTheLibraryChangedInAnotherSessionItsOwnSentenceIsKeptAndTheEditsStay()
    {
        _mutationError = "This library changed in another session. Reload before editing again.";
        var cut = Open();
        Switch(cut, "Use Recycle bin").Change(true);

        Button(cut, "Save changes").Click();

        cut.WaitForAssertion(() => Assert.Single(_mutations));
        Assert.Equal(0, _changed);
        Assert.NotEmpty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void OnAPhoneTheValuesAreShownButNoControlWorksAndThereIsNoSaveBar()
    {
        _library = Library(managed: true, tags: true);
        var cut = Open(readOnly: true);

        Assert.True(Switch(cut, "Rename or move files automatically").HasAttribute("checked"));
        Assert.True(Switch(cut, "Write metadata into media files").HasAttribute("checked"));
        Assert.All(cut.FindAll(".library-handling input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.All(cut.FindAll(".library-handling button.app-choice-row"), choice => Assert.True(choice.HasAttribute("disabled")));
        Assert.Empty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void AVaultLibraryCannotBeChangedHereAndSaysWhy()
    {
        _library = Library(vault: true);
        var cut = Open();

        Assert.Contains("Vault-protected", cut.Find(".app-alert").TextContent, StringComparison.Ordinal);
        Assert.True(Switch(cut, "Rename or move files automatically").HasAttribute("disabled"));
        Assert.True(Switch(cut, "Write metadata into media files").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".app-save-bar"));
    }

    [Fact]
    public void TheWriteMetadataSwitchLinksToTheGuideOnHowWritingWorks()
    {
        var cut = Open();

        var link = cut.FindAll("a").Single(anchor => Text(anchor) == "How writing metadata works");
        Assert.Contains("guides/library-settings/#when-changes-reach-your-files", link.GetAttribute("href"), StringComparison.Ordinal);
    }
}
