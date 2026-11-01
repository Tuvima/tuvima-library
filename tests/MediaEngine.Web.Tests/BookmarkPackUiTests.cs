using Bunit;
using MediaEngine.Contracts.Import;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// Bookmark packs in the video player (Videos spec D46): Export (choose, review, save), Import (check the title, then add; a different
// cut offers align by chapter or skip), pack bookmarks on the progress rail in their own shade, switchable off per pack, and no pack
// controls at all in the Vault.
public sealed class BookmarkPackUiTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid AssetId = Guid.NewGuid();

    public BookmarkPackUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/audiobook-bookmark-dialog.js");
        JSInterop.SetupModule("./js/playback-tool-sheet.js");
        JSInterop.SetupModule("./js/playback-seek-rail.js");
        Services.AddLogging();
    }

    private static AudiobookBookmarkDto Own(double seconds, string note, bool global = false, Guid? profile = null) => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = profile ?? Me,
        WorkId = WorkId,
        AssetId = AssetId,
        PositionSeconds = seconds,
        Note = note,
        IsGlobal = global,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static BookmarkPackDto Pack(string name = "Gondor", string? author = "A fan", bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = WorkId,
        Name = name,
        Author = author,
        IsEnabled = enabled,
        Bookmarks = [new PackBookmarkDto { Id = Guid.NewGuid(), PositionSeconds = 3010, EndSeconds = 3100, Note = "The charge" }],
    };

    private IRenderedComponent<BookmarkPackSection> RenderSection(
        IEngineApiClient api, IReadOnlyList<AudiobookBookmarkDto>? saved = null, IReadOnlyList<BookmarkPackDto>? packs = null,
        Action? changed = null, Func<double, bool, Task>? jump = null)
    {
        // The shared Import reads the file first and says it is a bookmark pack; the title's own check then decides if it fits.
        ((EngineApiClientStub)(object)api).SetHandler(nameof(IEngineApiClient.RecogniseImportAsync), _ =>
            Task.FromResult<ImportPreviewDto?>(new ImportPreviewDto
            {
                Kind = ImportKinds.BookmarkPack,
                KindLabel = "Bookmark pack",
                Recognised = true,
                Heading = "A pack",
            }));
        Services.AddSingleton(api);
        return Render<BookmarkPackSection>(parameters => parameters
            .Add(component => component.ProfileId, Me)
            .Add(component => component.WorkId, WorkId)
            .Add(component => component.AssetId, AssetId)
            .Add(component => component.Title, "The Film")
            .Add(component => component.Duration, 7200d)
            .Add(component => component.Saved, saved ?? [])
            .Add(component => component.Packs, packs ?? [])
            .Add(component => component.PacksChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => changed?.Invoke()))
            .Add(component => component.JumpTo, jump));
    }

    private static void ChoosePack(IRenderedComponent<BookmarkPackSection> cut, string content)
    {
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Import").Click();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(content, "pack.zip"));
    }

    [Fact]
    public void Export_IsOfferedOnlyWhenThereIsABookmarkOfYourOwn()
    {
        var api = EngineApiClientStub.Create(_ => { });

        var none = RenderSection(api, saved: [Own(100, "shared", global: true)]);
        Assert.True(none.FindAll("button").First(button => button.TextContent.Trim() == "Export").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Export_ChoosesThenReviewsThenSaves_SendingOnlyTheChosenOwnBookmarks()
    {
        BookmarkPackExportRequestDto? sent = null;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.ExportBookmarkPackAsync), args =>
        {
            sent = (BookmarkPackExportRequestDto)args![2]!;
            return Task.FromResult<BookmarkPackFileDto?>(new BookmarkPackFileDto
            {
                FileName = "bookmarks-gondor.zip",
                ContentBase64 = Convert.ToBase64String([1, 2, 3]),
                BookmarkCount = sent.BookmarkIds.Count,
            });
        }));
        var first = Own(120, "first");
        var second = Own(900, "second");
        var shared = Own(300, "for everyone", global: true);
        var cut = RenderSection(api, saved: [first, second, shared]);

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Export").ClickAsync();
        // Only the two personal bookmarks can be chosen: the shared one is not offered.
        Assert.Equal(2, cut.FindAll("input[type=checkbox]").Count);
        Assert.DoesNotContain("for everyone", cut.Markup);
        Assert.True(cut.FindAll("button").First(button => button.TextContent.Trim() == "Review").HasAttribute("disabled"));

        await cut.FindAll("input[type=checkbox]")[1].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Review").ClickAsync();
        Assert.Contains("does not hold file paths, profile names or what anyone watched", cut.Markup);
        Assert.Contains("second", cut.Markup);
        Assert.DoesNotContain("first", cut.Find(".bookmark-packs__list").TextContent);

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Save pack file").ClickAsync();

        Assert.NotNull(sent);
        Assert.Equal([second.Id], sent!.BookmarkIds);
        Assert.Equal(AssetId, sent.AssetId);
        Assert.Equal(7200, sent.RunningTimeSeconds);
        Assert.Equal("The Film bookmarks", sent.Name);
        Assert.Null(sent.Author);
        JSInterop.VerifyInvoke("tuvimaDownloads.fromStream");
        Assert.Contains("Saved a pack with 1 bookmark.", cut.Markup);
    }

    [Fact]
    public async Task Import_OfAPackForThisTitle_ShowsWhatIsInItAndAddsIt()
    {
        BookmarkPackImportRequestDto? imported = null;
        var added = Pack();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.InspectBookmarkPackAsync), _ => Task.FromResult<BookmarkPackPreviewDto?>(new BookmarkPackPreviewDto
            {
                Name = "Gondor",
                Author = "A fan",
                BookmarkCount = 3,
                Match = BookmarkPackMatchResults.Match,
                Reason = "This pack is for this title and cut.",
            }));
            stub.SetHandler(nameof(IEngineApiClient.ImportBookmarkPackAsync), args =>
            {
                imported = (BookmarkPackImportRequestDto)args![2]!;
                return Task.FromResult<BookmarkPackDto?>(added);
            });
        });
        var changes = 0;
        var cut = RenderSection(api, changed: () => changes++);

        ChoosePack(cut, "pack bytes");
        cut.WaitForAssertion(() => Assert.Contains("Gondor by A fan", cut.Markup));
        Assert.Contains("3 bookmarks", cut.Markup);
        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Add pack").ClickAsync();

        Assert.Equal(Convert.ToBase64String("pack bytes"u8.ToArray()), imported!.ContentBase64);
        Assert.Equal(BookmarkPackImportModes.AsIs, imported.Mode);
        Assert.Equal(1, changes);
        Assert.Contains("Added 1 bookmark from this pack.", cut.Markup);
    }

    [Fact]
    public async Task Import_OfAPackForAnotherTitle_OffersNothingToAdd()
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.InspectBookmarkPackAsync), _ =>
            Task.FromResult<BookmarkPackPreviewDto?>(new BookmarkPackPreviewDto
            {
                Name = "Other film",
                BookmarkCount = 2,
                Match = BookmarkPackMatchResults.WrongTitle,
                Reason = "This pack is for a different title.",
            })));
        var cut = RenderSection(api);

        ChoosePack(cut, "x");

        cut.WaitForAssertion(() => Assert.Contains("This pack is for a different title.", cut.Markup));
        var buttons = cut.FindAll(".bookmark-packs__panel button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Equal(["Close"], buttons);
    }

    [Fact]
    public async Task Import_OfADifferentCut_SaysSo_AndOffersAlignByChapterOrSkip()
    {
        BookmarkPackImportRequestDto? imported = null;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.InspectBookmarkPackAsync), _ => Task.FromResult<BookmarkPackPreviewDto?>(new BookmarkPackPreviewDto
            {
                Name = "Gondor",
                BookmarkCount = 2,
                Match = BookmarkPackMatchResults.DifferentCut,
                CanAlignByChapter = true,
                Reason = "This pack was made on a cut that runs 3:00:00; your copy runs 3:25:00.",
            }));
            stub.SetHandler(nameof(IEngineApiClient.ImportBookmarkPackAsync), args =>
            {
                imported = (BookmarkPackImportRequestDto)args![2]!;
                return Task.FromResult<BookmarkPackDto?>(Pack() with { IsAligned = true });
            });
        });
        var cut = RenderSection(api);

        ChoosePack(cut, "x");
        cut.WaitForAssertion(() => Assert.Contains("runs 3:00:00", cut.Markup));
        var buttons = cut.FindAll(".bookmark-packs__panel button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Equal(["Align by chapter", "Skip"], buttons);

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Align by chapter").ClickAsync();
        Assert.Equal(BookmarkPackImportModes.AlignByChapter, imported!.Mode);
    }

    [Fact]
    public async Task Skip_AddsNothing()
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.InspectBookmarkPackAsync), _ =>
            Task.FromResult<BookmarkPackPreviewDto?>(new BookmarkPackPreviewDto
            {
                Name = "Gondor",
                BookmarkCount = 2,
                Match = BookmarkPackMatchResults.DifferentCut,
                CanAlignByChapter = true,
                Reason = "Different cut.",
            })));
        var cut = RenderSection(api);
        ChoosePack(cut, "x");
        cut.WaitForAssertion(() => Assert.Contains("Different cut.", cut.Markup));

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Skip").ClickAsync();

        Assert.Empty(cut.FindAll(".bookmark-packs__panel"));
        Assert.DoesNotContain(nameof(IEngineApiClient.ImportBookmarkPackAsync), ((EngineApiClientStub)(object)api).Calls);
    }

    [Fact]
    public async Task APack_ListsItsBookmarksAsReadOnlyRows_CanBePlayed_SwitchedOffAndRemoved()
    {
        var pack = Pack();
        var switched = new List<(Guid Pack, bool On)>();
        var removed = new List<Guid>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.SetBookmarkPackEnabledAsync), args =>
            {
                switched.Add(((Guid)args![0]!, (bool)args[2]!));
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteBookmarkPackAsync), args =>
            {
                removed.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
        });
        var jumps = new List<(double, bool)>();
        var changes = 0;
        var cut = RenderSection(api, packs: [pack], changed: () => changes++, jump: (seconds, play) => { jumps.Add((seconds, play)); return Task.CompletedTask; });

        Assert.Contains("Gondor by A fan", cut.Markup);
        Assert.Contains("50:10 to 51:40", cut.Markup);
        Assert.Contains("The charge", cut.Markup);
        // Pack bookmarks are read-only: no delete or edit control on a row.
        Assert.Single(cut.FindAll(".bookmark-packs__list button"));

        await cut.Find(".bookmark-packs__play").ClickAsync();
        Assert.Equal((3010d, true), Assert.Single(jumps));

        await cut.Find("input[role=switch]").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });
        Assert.Equal((pack.Id, false), Assert.Single(switched));

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Remove pack").ClickAsync();
        Assert.Equal(pack.Id, Assert.Single(removed));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ARailTick_ForAPackBookmark_HasItsOwnShadeAndNamesThePack_AndASwitchedOffPackDrawsNone()
    {
        var on = Pack("Gondor", "A fan");
        var off = Pack("Hidden", null, enabled: false);

        var ticks = PlaybackRailBookmark.FromPacks([on, off], 7200);

        var tick = Assert.Single(ticks);
        Assert.True(tick.IsPack);
        Assert.False(tick.IsGlobal);
        Assert.Equal("Gondor by A fan", tick.PackLabel);

        var cut = Render<PlaybackSeekRail>(parameters => parameters
            .Add(component => component.Position, 0d)
            .Add(component => component.Duration, 7200d)
            .Add(component => component.Bookmarks, ticks));
        var button = cut.Find("button.playback-seek-rail__bookmark");
        Assert.Contains("is-pack", button.ClassList);
        Assert.Contains("Pack bookmark (Gondor by A fan)", button.GetAttribute("aria-label"));
        cut.Find(".playback-seek-rail__bookmark-wrap").MouseEnter();
        Assert.Contains("Gondor by A fan", cut.Find(".playback-seek-rail__quicklook").TextContent);
    }

    [Fact]
    public async Task ThePanel_ShowsPackControlsInSaved_ButNoneInTheVault()
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
            Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded([])))));

        async Task<IRenderedComponent<VideoBookmarkPanel>> OpenSavedAsync(bool allowPacks)
        {
            var cut = Render<VideoBookmarkPanel>(parameters => parameters
                .Add(component => component.ProfileId, Me)
                .Add(component => component.WorkId, WorkId)
                .Add(component => component.AssetId, AssetId)
                .Add(component => component.Position, 750d)
                .Add(component => component.Duration, 7200d)
                .Add(component => component.AllowPacks, allowPacks));
            await cut.Find(".video-bookmark-quick__more").ClickAsync();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.audiobook-bookmark-dialog__tab")));
            await cut.FindAll("button.audiobook-bookmark-dialog__tab").First(tab => tab.TextContent.Trim() == "Saved").ClickAsync();
            return cut;
        }

        var normal = await OpenSavedAsync(allowPacks: true);
        normal.WaitForAssertion(() => Assert.NotEmpty(normal.FindAll(".bookmark-packs")));

        var vault = await OpenSavedAsync(allowPacks: false);
        vault.WaitForAssertion(() => Assert.NotEmpty(vault.FindAll(".audiobook-bookmark-dialog__saved-toolbar")));
        Assert.Empty(vault.FindAll(".bookmark-packs"));
        Assert.DoesNotContain("Export", vault.Markup);
        Assert.DoesNotContain("Import", vault.Markup);
    }
}
