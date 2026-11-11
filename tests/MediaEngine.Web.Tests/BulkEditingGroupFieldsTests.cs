using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Several items by rules, the group fields and the shared cover (universal editor v4, spec 3c, acceptance A7): ticking 12 songs offers
/// album artist, genre, year, disc and Explicit but never title or track number, a change reaches all 12, the album's cover is chosen once
/// and said in words, and the header shows small pictures when the rows carry them. The Engine is a hand-written stub.
/// </summary>
public sealed class BulkEditingGroupFieldsTests : AsyncBunitContext
{
    private static readonly Guid Album = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherAlbum = Guid.Parse("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid CurrentCover = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid NewCover = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid UploadedCover = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private ArtworkEntityWorkspaceDto _workspace;
    private readonly List<object?[]> _uploads = [];

    public BulkEditingGroupFieldsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        _workspace = Workspace((CurrentCover, true), (NewCover, false));
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), args =>
            {
                var id = ((LibraryBatchEditPreviewRequest)args![0]!).EntityIds.Single();
                return Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(new LibraryBatchEditPreview
                {
                    AffectedCount = 1,
                    Fields = [.. CatalogFor(id)],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEntityArtworkAsync), _ => Task.FromResult<ArtworkEntityWorkspaceDto?>(_workspace));
            stub.SetHandler(nameof(IEngineApiClient.UploadCanonicalArtworkAsync), args =>
            {
                _uploads.Add(args!);
                _workspace = Workspace((CurrentCover, true), (NewCover, false), (UploadedCover, false));
                return Task.FromResult<ArtworkAssetDto?>(new ArtworkAssetDto(UploadedCover, "/art/up/large", "/art/up/small", 1000, 1000, "Square", "user_upload", null, []));
            });
        }));
    }

    private static ArtworkEntityWorkspaceDto Workspace(params (Guid Asset, bool Current)[] pictures) =>
        new(Album, "Work", pictures.Select(p => new ArtworkEntityVariantDto(
            Guid.NewGuid(), p.Asset, "Primary", null, "CoverArt", p.Current, false,
            $"/art/{p.Asset:N}/large", $"/art/{p.Asset:N}/small", 1000, 1000, "Square", "musicbrainz", null)).ToList());

    private static Guid SongId(int number) => Guid.Parse($"50000000-0000-0000-0000-{number:D12}");

    private static IReadOnlyList<BulkEditRow> TwelveSongs(Guid? album = null, bool pictures = false) =>
    [
        .. Enumerable.Range(1, 12).Select(number => new BulkEditRow(
            SongId(number), $"Track {number}", DetailEntityType.Work, album ?? Album, "Music", pictures ? $"/art/song-{number}.jpg" : null)),
    ];

    // What the Engine's batch edit catalog reports for one song: every group field, and two items that name only one song.
    private static IEnumerable<LibraryBatchFieldState> CatalogFor(Guid id)
    {
        var tail = id.ToString()[^1].ToString();
        yield return Field("genre", "Genres", LibraryBatchFieldKinds.List, [LibraryBatchEditOps.Add, LibraryBatchEditOps.Replace, LibraryBatchEditOps.Remove], values: ["Rock"]);
        yield return Field("custom_tags", "Tags", LibraryBatchFieldKinds.List, [LibraryBatchEditOps.Add, LibraryBatchEditOps.Replace, LibraryBatchEditOps.Remove]);
        yield return Field("year", "Year", LibraryBatchFieldKinds.Scalar, [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear], value: "2001");
        yield return Field("album_artist", "Album artist", LibraryBatchFieldKinds.List, [LibraryBatchEditOps.Add, LibraryBatchEditOps.Replace, LibraryBatchEditOps.Remove], values: ["Radiohead"]);
        yield return Field("disc_number", "Disc", LibraryBatchFieldKinds.Scalar, [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear], value: "1");
        yield return Field("explicit", "Explicit", LibraryBatchFieldKinds.Scalar, [LibraryBatchEditOps.Set]);
        yield return Field("series", "Series", LibraryBatchFieldKinds.Scalar, [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear], value: "series-" + tail);
        yield return Field("track_number", "Track", LibraryBatchFieldKinds.Scalar, [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear], value: tail);
        yield return Field("title", "Title", LibraryBatchFieldKinds.Scalar, [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear], value: "t" + tail);
    }

    private static LibraryBatchFieldState Field(
        string key, string label, string kind, IEnumerable<string> operations, string? value = null, IEnumerable<string>? values = null) => new()
        {
            Key = key,
            Label = label,
            Kind = kind,
            Operations = [.. operations],
            Editable = true,
            Value = value,
            Values = [.. values ?? []],
        };

    private async Task<(IRenderedComponent<BulkEditPanel> Panel, PendingEditList Pending, BulkEditSelection Selection)> OpenAsync(IReadOnlyList<BulkEditRow> rows)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var selection = new BulkEditSelection { MediaType = "Music" };
        selection.Replace(rows);
        selection.OpenPanel(BulkPanelKind.Edit);
        var pending = new PendingEditList();
        var panel = Render<BulkEditPanel>(parameters => parameters
            .Add(component => component.Selection, selection)
            .Add(component => component.Pending, pending));
        panel.WaitForElement("[data-field-key='year']");
        return (panel, pending, selection);
    }

    // -- A7: 12 songs ----------------------------------------------------------------------------

    [Fact]
    public async Task TwelveSongs_OfferAlbumArtistGenreYearDiscAndExplicit_ButNotTitleOrTrackNumber()
    {
        var (panel, _, _) = await OpenAsync(TwelveSongs());

        foreach (var key in new[] { "album_artist", "genre", "year", "disc_number", "explicit", "custom_tags" })
        {
            Assert.NotEmpty(panel.FindAll($"[data-field-key='{key}']"));
        }

        Assert.Empty(panel.FindAll("[data-field-key='title']"));
        Assert.Empty(panel.FindAll("[data-field-key='track_number']"));
        Assert.Empty(panel.FindAll("[data-field-key='series']"));
        Assert.Contains("Editing 12 songs", panel.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwelveSongs_TheProviderFieldsStartLocked_ButTagsAndTheExplicitMarkAreTheirOwn()
    {
        var (panel, _, _) = await OpenAsync(TwelveSongs());

        foreach (var key in new[] { "album_artist", "genre", "year", "disc_number" })
        {
            Assert.Equal("true", panel.Find($"[data-field-key='{key}']").GetAttribute("data-locked"));
        }

        foreach (var key in new[] { "custom_tags", "explicit" })
        {
            var own = panel.Find($"[data-field-key='{key}']");
            Assert.Equal("false", own.GetAttribute("data-locked"));
            Assert.Empty(own.QuerySelectorAll("[data-lock-state]"));
        }
    }

    [Fact]
    public async Task TwelveSongs_SavingAlbumArtistAndDisc_StagesAChangeForEverySong()
    {
        var (panel, pending, _) = await OpenAsync(TwelveSongs());

        panel.UnlockField("disc_number");
        panel.Find("[data-field-key='disc_number'] input").Input("2");
        panel.UnlockField("album_artist");
        var artist = panel.Find("[data-field-key='album_artist']");
        artist.QuerySelector("input[aria-label='Tag to add']")!.Input("Various Artists");
        artist.QuerySelector("form")!.Submit();

        for (var number = 1; number <= 12; number++)
        {
            Assert.True(pending.TryGetField(SongId(number), "disc_number", out var disc));
            Assert.Equal("2", disc);
            Assert.True(pending.TryGetField(SongId(number), "album_artist", out var people));
            Assert.Equal("Radiohead; Various Artists", people);
        }

        var details = pending.ToRequest(previewOnly: false).Changes.Where(change => change.Kind == EditSessionChangeKinds.Details).ToList();
        Assert.Equal(12, details.Count);
        Assert.All(details, change => Assert.Equal(["album_artist", "disc_number"], change.Fields.Select(field => field.Key).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Explicit_IsASwitchThatStagesTheMarkOnEverySong()
    {
        var (panel, pending, _) = await OpenAsync(TwelveSongs());

        var group = panel.Find("[data-field-key='explicit']");
        Assert.NotNull(group.QuerySelector("[role='group'][aria-label='Explicit']"));
        Assert.Empty(group.QuerySelectorAll("input[type='text']"));
        var on = group.QuerySelectorAll("button").Single(button => button.TextContent.Trim() == "Explicit");
        on.Click();

        for (var number = 1; number <= 12; number++)
        {
            Assert.True(pending.TryGetField(SongId(number), "explicit", out var mark));
            Assert.Equal("true", mark);
        }
    }

    [Fact]
    public void Explicit_OffOnSongsNeverMarkedStagesNothing()
    {
        var reads = Enumerable.Range(1, 3).ToDictionary(
            number => SongId(number),
            number => (IReadOnlyList<LibraryBatchFieldState>)[Field("explicit", "Explicit", LibraryBatchFieldKinds.Scalar, [LibraryBatchEditOps.Set])]);
        var view = BulkEditFields.Build([SongId(1), SongId(2), SongId(3)], reads).Single();
        var pending = new PendingEditList();

        var result = BulkEditStager.StageScalar(pending, view, "false", "songs");

        Assert.False(view.IsMixed);
        Assert.Equal(0, result.Staged);
        Assert.Equal(0, pending.ChangeCount);
    }

    [Fact]
    public void Explicit_IsOfferedForSongsAndVideosOnly_AndAuthorAndSeriesForBooksOnly()
    {
        Assert.Contains("explicit", BulkEditRules.GroupFieldKeys(BulkItemKind.Song));
        Assert.Contains("explicit", BulkEditRules.GroupFieldKeys(BulkItemKind.Video));
        Assert.DoesNotContain("explicit", BulkEditRules.GroupFieldKeys(BulkItemKind.Book));
        Assert.DoesNotContain("explicit", BulkEditRules.GroupFieldKeys(BulkItemKind.Episode));
        Assert.Equal(["series", "author", "custom_tags"], BulkEditRules.GroupFieldKeys(BulkItemKind.Book));
        Assert.Equal(["album", "album_artist", "genre", "year", "disc_number", "custom_tags", "explicit"], BulkEditRules.GroupFieldKeys(BulkItemKind.Song));
    }

    [Fact]
    public void Author_AddedToBooks_StagesEachBooksOwnListPlusTheNewName()
    {
        var reads = new Dictionary<Guid, IReadOnlyList<LibraryBatchFieldState>>
        {
            [SongId(1)] = [Field("author", "Author", LibraryBatchFieldKinds.List, [LibraryBatchEditOps.Add, LibraryBatchEditOps.Replace, LibraryBatchEditOps.Remove], values: ["Frank Herbert"])],
            [SongId(2)] = [Field("author", "Author", LibraryBatchFieldKinds.List, [LibraryBatchEditOps.Add, LibraryBatchEditOps.Replace, LibraryBatchEditOps.Remove])],
        };
        var view = BulkEditFields.Build([SongId(1), SongId(2)], reads).Single();
        var pending = new PendingEditList();

        var result = BulkEditStager.StageList(pending, view, LibraryBatchEditOps.Add, ["Brian Herbert"]);

        Assert.Equal(2, result.Staged);
        Assert.True(pending.TryGetField(SongId(1), "author", out var first));
        Assert.Equal("Frank Herbert; Brian Herbert", first);
        Assert.True(pending.TryGetField(SongId(2), "author", out var second));
        Assert.Equal("Brian Herbert", second);
    }

    // -- Artwork for several -----------------------------------------------------------------------

    [Fact]
    public void Artwork_IsOnlyForSongsOfOneAlbum_AndSaidInWords()
    {
        Assert.Equal(Album, BulkEditRules.SharedArtworkTarget(TwelveSongs(), "Music"));
        Assert.Null(BulkEditRules.SharedArtworkTarget([.. TwelveSongs().Take(11), new BulkEditRow(SongId(12), "x", DetailEntityType.Work, OtherAlbum, "Music")], "Music"));
        Assert.Null(BulkEditRules.SharedArtworkTarget([new BulkEditRow(SongId(1), "x", DetailEntityType.Work, null, "Music")], "Music"));
        Assert.Null(BulkEditRules.SharedArtworkTarget([new BulkEditRow(SongId(1), "x", DetailEntityType.TvEpisode, Album, "TV")], "TV"));
        Assert.Equal("Set one cover for these 12 songs. It sets the album's cover.", BulkEditRules.ArtworkSentence(BulkItemKind.Song, 12));
        Assert.Null(BulkEditRules.ArtworkSentence(BulkItemKind.Book, 3));
        Assert.Null(BulkEditRules.ArtworkSentence(BulkItemKind.Video, 3));
    }

    [Fact]
    public async Task Panel_ForTwelveSongsOfOneAlbum_ShowsTheCoverChoiceAndStagesItOnceOnTheAlbum()
    {
        var (panel, pending, _) = await OpenAsync(TwelveSongs());

        panel.WaitForAssertion(() => Assert.Equal(2, panel.FindAll("[data-bulk-artwork] .tl-bulk__cover").Count));
        Assert.Contains("Set one cover for these 12 songs. It sets the album's cover.", panel.Find("[data-bulk-artwork]").TextContent, StringComparison.Ordinal);

        panel.FindAll("[data-bulk-artwork] .tl-bulk__cover")[1].Click();

        var change = Assert.Single(pending.Changes, candidate => candidate.Kind == EditSessionChangeKinds.Artwork);
        Assert.Equal(Album, change.EntityId);
        Assert.Equal("CoverArt", change.Slot);
        Assert.Equal(NewCover.ToString("D"), change.TargetId);
        panel.WaitForAssertion(() => Assert.Contains("The album's cover will change when you save.", panel.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Panel_ForSongsOfDifferentAlbums_OffersNoCoverChoice()
    {
        var rows = new List<BulkEditRow>(TwelveSongs().Take(6)) { new(SongId(7), "Other", DetailEntityType.Work, OtherAlbum, "Music") };

        var (panel, _, _) = await OpenAsync(rows);

        Assert.Empty(panel.FindAll("[data-bulk-artwork]"));
    }

    [Fact]
    public async Task Picker_ChoosingTheCoverTheAlbumAlreadyHas_TakesTheStagedChoiceBack()
    {
        var pending = new PendingEditList();
        var picker = new BulkArtworkPicker(Services.GetRequiredService<IEngineApiClient>(), pending);
        await picker.LoadAsync(Album);

        var current = picker.Pictures.Single(picture => picture.IsCurrent);
        var other = picker.Pictures.Single(picture => !picture.IsCurrent);
        picker.Pick(other);
        Assert.True(picker.HasStaged);
        Assert.Equal(NewCover, picker.StagedAssetId);

        picker.Pick(current);

        Assert.False(picker.HasStaged);
        Assert.Equal(0, pending.ChangeCount);
    }

    [Fact]
    public async Task Picker_AnUploadedPicture_IsStoredBesideTheAlbumAndStagedAsItsCover()
    {
        var pending = new PendingEditList();
        var picker = new BulkArtworkPicker(Services.GetRequiredService<IEngineApiClient>(), pending);
        await picker.LoadAsync(Album);

        var ok = await picker.UploadAsync(new FakeFile("cover.png", "image/png", 1234));

        Assert.True(ok);
        var upload = Assert.Single(_uploads);
        Assert.Equal(Album, upload[1]);
        Assert.Equal(UploadedCover, picker.StagedAssetId);
        Assert.Equal(1, pending.ChangeCount);
    }

    [Fact]
    public async Task Picker_APictureThatIsNotJpegOrPng_IsRefusedWithAReason()
    {
        var picker = new BulkArtworkPicker(Services.GetRequiredService<IEngineApiClient>(), new PendingEditList());
        await picker.LoadAsync(Album);

        var ok = await picker.UploadAsync(new FakeFile("cover.gif", "image/gif", 10));

        Assert.False(ok);
        Assert.Equal("Choose a JPEG or PNG image.", picker.Message);
        Assert.Empty(_uploads);
    }

    // -- Header pictures -----------------------------------------------------------------------------

    [Fact]
    public async Task Header_ShowsSmallPicturesWhenTheRowsCarryThem_AndNoneWhenTheyDoNot()
    {
        var (withPictures, _, _) = await OpenAsync(TwelveSongs(pictures: true));
        Assert.Equal(8, withPictures.FindAll("[data-bulk-faces] img").Count);
        Assert.Contains("+4", withPictures.Find("[data-bulk-faces]").TextContent, StringComparison.Ordinal);

        var (without, _, _) = await OpenAsync(TwelveSongs());
        Assert.Empty(without.FindAll("[data-bulk-faces]"));
    }

    private sealed class FakeFile(string name, string contentType, long size) : IBrowserFile
    {
        public string Name { get; } = name;

        public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;

        public long Size { get; } = size;

        public string ContentType { get; } = contentType;

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream([1, 2, 3]);
    }
}
