using Bunit;
using MediaEngine.Contracts.Vault;
using MediaEngine.Web.Components.Vault;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Vault;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Identify and the Vault activity list in the Dashboard (Videos spec WP12, D32 and D38). The rules under test: nothing is drawn
/// unless Vault mode is on (and, for Identify, on a desktop); the person ticks fields one by one; a value they edited themselves
/// cannot be ticked; only the ticked fields are sent; "None of these" sends nothing; and a missing key reads "not connected".
/// </summary>
public sealed class VaultIdentifyPanelTests : AsyncBunitContext
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Asset = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private bool _unlocked = true;
    private string _searchStatus = "ok";
    private VaultOutcome _activityOutcome = VaultOutcome.Ok;
    private IReadOnlyList<VaultActivityItemDto> _activity = [];
    private int _waitingBookmarks;
    private readonly List<string?> _searches = [];
    private readonly List<VaultIdentifyPreviewRequest> _previews = [];
    private readonly List<VaultIdentifyApplyRequest> _applies = [];
    private EngineApiClientStub _stub = default!;

    public VaultIdentifyPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton(_device);
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private async Task UseEngineAsync()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            _stub = stub;
            stub.SetHandler(nameof(IEngineApiClient.GetNsfwStatusAsync), _ =>
                Task.FromResult(VaultCall<NsfwStatusDto>.Ok(new NsfwStatusDto { Mode = "allowed", Unlocked = _unlocked, HasPin = false, IdleLockMinutes = 15 })));
            stub.SetHandler(nameof(IEngineApiClient.SearchVaultIdentifyAsync), args =>
            {
                _searches.Add((string?)args![1]);
                return Task.FromResult(VaultCall<VaultCandidateListDto>.Ok(new VaultCandidateListDto
                {
                    Status = _searchStatus,
                    Candidates = _searchStatus == "ok"
                        ? [
                            new VaultCandidateDto { ServerId = "s1", RecordId = "r-good", Title = "Right Record", LengthAgrees = true, DurationSeconds = 1800 },
                            new VaultCandidateDto { ServerId = "s1", RecordId = "r-off", Title = "Other Record", LengthAgrees = false },
                        ]
                        : [],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewVaultIdentifyAsync), args =>
            {
                var request = (VaultIdentifyPreviewRequest)args![1]!;
                _previews.Add(request);
                return Task.FromResult(VaultCall<VaultIdentifyPreviewDto>.Ok(new VaultIdentifyPreviewDto
                {
                    Status = "ok",
                    ServerId = request.ServerId,
                    RecordId = request.RecordId,
                    RecordTitle = "Right Record",
                    LengthAgrees = true,
                    Fields =
                    [
                        new VaultFieldChoiceDto { Field = "title", Current = "file name", Proposed = "Right Record", Changes = true, Ticked = true },
                        new VaultFieldChoiceDto { Field = "studio", Current = null, Proposed = "Studio A", Changes = true, Ticked = true },
                        new VaultFieldChoiceDto { Field = "description", Current = "Mine", Proposed = "Theirs", Changes = true, PersonsOwn = true, Ticked = false },
                    ],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.ApplyVaultIdentifyAsync), args =>
            {
                var request = (VaultIdentifyApplyRequest)args![1]!;
                _applies.Add(request);
                return Task.FromResult(VaultCall<VaultIdentifyApplyResultDto>.Ok(new VaultIdentifyApplyResultDto
                {
                    Status = "ok",
                    Applied = [.. request.Fields],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetVaultRestoredBookmarksAsync), _ =>
                Task.FromResult(VaultCall<VaultRestoredBookmarksDto>.Ok(new VaultRestoredBookmarksDto
                {
                    Waiting = _waitingBookmarks,
                    Message = _waitingBookmarks == 0 ? string.Empty : $"{_waitingBookmarks} saved bookmarks are still waiting for a matching video.",
                })));
            stub.SetHandler(nameof(IEngineApiClient.GetVaultActivityAsync), _ =>
                Task.FromResult(_activityOutcome == VaultOutcome.Ok
                    ? VaultCall<IReadOnlyList<VaultActivityItemDto>>.Ok(_activity)
                    : VaultCall<IReadOnlyList<VaultActivityItemDto>>.Of(_activityOutcome)));
        });
        Services.AddSingleton(api);
        await Services.GetRequiredService<NsfwModeState>().RefreshAsync(Me, isKids: false);
    }

    [Fact]
    public async Task Identify_IsNotDrawn_WhenNsfwModeIsOff_OrOnADeviceThatCannotEdit()
    {
        _unlocked = false;
        await UseEngineAsync();
        await UseDesktopAsync();
        Assert.Equal(string.Empty, Render<VaultIdentifyPanel>(p => p.Add(x => x.AssetId, Asset)).Markup.Trim());

        _unlocked = true;
        await Services.GetRequiredService<NsfwModeState>().RefreshAsync(Me, isKids: false);
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "phone", true, FinePointer: false, Hover: false));
        Assert.Equal(string.Empty, Render<VaultIdentifyPanel>(p => p.Add(x => x.AssetId, Asset)).Markup.Trim());
    }

    [Fact]
    public async Task SearchingListsCandidates_AndNotesWhenALengthDiffers()
    {
        await UseEngineAsync();
        await UseDesktopAsync();
        var cut = Render<VaultIdentifyPanel>(p => p.Add(x => x.AssetId, Asset));

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Search")).Click());

        Assert.Equal([null], _searches);
        Assert.Equal(2, cut.FindAll(".vault-identify__candidate").Count);
        Assert.Contains("Length matches this file", cut.Markup);
        Assert.Contains("Length differs from this file", cut.Markup);
    }

    [Fact]
    public async Task ChoosingShowsTickedFields_ThePersonsOwnEditCannotBeTicked_AndOnlyTickedFieldsAreApplied()
    {
        await UseEngineAsync();
        await UseDesktopAsync();
        var applied = 0;
        var cut = Render<VaultIdentifyPanel>(p => p
            .Add(x => x.AssetId, Asset)
            .Add(x => x.OnApplied, () => applied++));
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Search")).Click());
        await cut.InvokeAsync(() => cut.FindAll(".vault-identify__candidate button").First().Click());

        Assert.Equal("r-good", Assert.Single(_previews).RecordId);
        var description = cut.Find("tr[data-field='description'] input[type=checkbox]");
        Assert.True(description.HasAttribute("disabled"));
        Assert.Contains("You edited this yourself", cut.Markup);

        // Untick the studio, leaving only the title.
        await cut.InvokeAsync(() => cut.Find("tr[data-field='studio'] input[type=checkbox]").Change(false));
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Apply ticked")).Click());

        var request = Assert.Single(_applies);
        Assert.Equal(["title"], request.Fields);
        Assert.Equal("r-good", request.RecordId);
        Assert.Equal(1, applied);
        Assert.Contains("Applied: title.", cut.Markup);
    }

    [Fact]
    public async Task NoneOfThese_SendsNothing_AndLeavesTheVideoUnmatched()
    {
        await UseEngineAsync();
        await UseDesktopAsync();
        var cut = Render<VaultIdentifyPanel>(p => p.Add(x => x.AssetId, Asset));
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Search")).Click());

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("None of these")).Click());

        Assert.Empty(_previews);
        Assert.Empty(_applies);
        Assert.Contains("stays Unmatched", cut.Markup);
        Assert.Empty(cut.FindAll(".vault-identify__candidate"));
    }

    [Theory]
    [InlineData("not_connected", "Not connected")]
    [InlineData("lookup_off", "does not look videos up")]
    public async Task AMissingKeyOrAnOffLibrary_SaysSo_InPlainWords(string status, string expected)
    {
        _searchStatus = status;
        await UseEngineAsync();
        await UseDesktopAsync();
        var cut = Render<VaultIdentifyPanel>(p => p.Add(x => x.AssetId, Asset));

        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Search")).Click());

        Assert.Contains(expected, cut.Markup);
        Assert.Empty(cut.FindAll(".vault-identify__candidate"));
        Assert.DoesNotContain("Tuvima", cut.Markup);
    }

    [Fact]
    public async Task TheActivityList_ShowsOneQuietLinePerFile_OnlyWhileNsfwModeIsOn()
    {
        _activity =
        [
            new VaultActivityItemDto { Id = Guid.NewGuid(), AssetId = Asset, Kind = "matched", FileName = "clip.mp4", Detail = "By file fingerprint", At = DateTimeOffset.UtcNow },
        ];
        await UseEngineAsync();

        var cut = Render<VaultActivityList>();

        var line = Assert.Single(cut.FindAll(".vault-activity__line"));
        Assert.Contains("Matched", line.TextContent);
        Assert.Contains("clip.mp4", line.TextContent);

        _unlocked = false;
        await Services.GetRequiredService<NsfwModeState>().RefreshAsync(Me, isKids: false);
        Assert.Equal(string.Empty, Render<VaultActivityList>().Markup.Trim());
    }

    [Fact]
    public async Task TheActivityList_SaysHowManyRestoredBookmarksStillWaitForAVideo_AndNothingWhenNoneDo()
    {
        await UseEngineAsync();
        Assert.Empty(Render<VaultActivityList>().FindAll("[data-vault-waiting-bookmarks]"));

        _waitingBookmarks = 3;
        var cut = Render<VaultActivityList>();

        Assert.Contains("3 saved bookmarks are still waiting", Assert.Single(cut.FindAll("[data-vault-waiting-bookmarks]")).TextContent);
    }

    [Fact]
    public async Task TheActivityList_DrawsNothing_WhenTheEngineAnswersNotFound()
    {
        _activityOutcome = VaultOutcome.NotAvailable;
        await UseEngineAsync();

        var cut = Render<VaultActivityList>();

        Assert.Equal(string.Empty, cut.Markup.Trim());
        Assert.Contains(nameof(IEngineApiClient.GetVaultActivityAsync), _stub.Calls);
    }
}
