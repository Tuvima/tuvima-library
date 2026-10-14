using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class ViewDocumentInboxUiTests : AsyncBunitContext
{
    private static readonly Guid LibraryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FirstId = Guid.Parse("21111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-1111-1111-1111-111111111111");

    private const string Suggestions = "\"suggestions\":{\"document_date\":\"2026-03-02\",\"date_from_file\":false,\"correspondent\":\"City Water\","
        + "\"document_type\":\"Invoice\",\"tags\":[\"Utilities\",\"Home\"],\"amount\":120.5,\"currency\":\"EUR\"}";

    private readonly InboxHandler _handler = new();
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private readonly IRenderedComponent<AppToastHost> _toasts;

    public ViewDocumentInboxUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddScoped<ActiveProfileAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)));
        _dialogs = Render<AppDialogHost>();
        _toasts = Render<AppToastHost>();
    }

    [Fact]
    public void TheRailOffersTheInboxFirstWithItsCountAndKeepsAllDocumentsAsTheDefault()
    {
        _handler.InboxCount = 3;
        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Inbox (3 to file)", cut.Find(".view-documents-rail").TextContent, StringComparison.Ordinal));
        var choices = cut.FindAll(".view-documents-rail [role=radio]");
        Assert.Equal(3, choices.Count);
        Assert.StartsWith("Inbox", choices[0].TextContent.Trim(), StringComparison.Ordinal);
        Assert.Equal("All documents", choices[1].TextContent.Trim());
        Assert.Equal("Trash", choices[2].TextContent.Trim());
        Assert.Equal("false", choices[0].GetAttribute("aria-checked"));
        Assert.Equal("true", choices[1].GetAttribute("aria-checked"));
        Assert.Empty(cut.FindAll(".view-inbox-card"));
        Assert.DoesNotContain(_handler.Requests, request => request.Contains("filing=inbox", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[style]"));
    }

    [Fact]
    public void PickingTheInboxAsksTheEngineForDocumentsWaitingToBeFiled()
    {
        var cut = RenderInbox();

        var request = _handler.Requests.Last(value => value.StartsWith("/view/assets", StringComparison.Ordinal));
        Assert.Contains("filing=inbox", request, StringComparison.Ordinal);
        Assert.Contains("kind=document", request, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find(".view-documents-rail [role=radio]").GetAttribute("aria-checked"));
    }

    [Fact]
    public void AnInboxCardShowsThePictureTitleDetectedDateAndEverySuggestionAsAChip()
    {
        var cut = RenderInbox();

        var card = cut.Find(".view-inbox-card");
        Assert.NotNull(card.QuerySelector("img"));
        Assert.Contains("Water bill", card.TextContent, StringComparison.Ordinal);
        var detected = new DateOnly(2026, 3, 2).ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal($"Detected {detected}", card.QuerySelector("time")!.TextContent.Trim());
        var chips = card.QuerySelectorAll(".view-inbox-chip").Select(chip => chip.TextContent.Trim()).ToList();
        Assert.Contains("City Water", chips);
        Assert.Contains("Invoice", chips);
        Assert.Contains("Utilities", chips);
        Assert.Contains("Home", chips);
        Assert.Contains("Amount 120.50 EUR", chips);
        Assert.Contains("Add tag", chips);
        Assert.Empty(cut.FindAll("[style]"));
    }

    [Fact]
    public void ADateTakenFromTheFilesOwnTimeSaysSo()
    {
        _handler.Inbox = "{\"items\":[" + InboxItem(FirstId, "Water bill",
            "\"suggestions\":{\"document_date\":\"2026-03-02\",\"date_from_file\":true}") + "],\"next_cursor\":null,\"has_more\":false}";
        var cut = RenderInbox();

        Assert.StartsWith("File date", cut.Find(".view-inbox-card time").TextContent.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void AValueARuleAlreadySetWinsOverTheSuggestionForThatField()
    {
        _handler.Inbox = "{\"items\":[" + InboxItem(FirstId, "Water bill",
            "\"correspondent\":{\"id\":\"bbbbbbbb-0000-0000-0000-000000000001\",\"name\":\"Acme\"}," + Suggestions) + "],\"next_cursor\":null,\"has_more\":false}";
        var cut = RenderInbox();

        var chips = cut.FindAll(".view-inbox-chip").Select(chip => chip.TextContent.Trim()).ToList();
        Assert.Contains("Acme", chips);
        Assert.DoesNotContain("City Water", chips);
        Assert.Contains("Invoice", chips);
    }

    [Fact]
    public async Task FileAcceptsTheSuggestionsRemovesTheCardRefreshesTheCountAndSaysFiled()
    {
        _handler.InboxCount = 2;
        var cut = RenderInbox();
        var summaries = _handler.Requests.Count(request => request.StartsWith("/view/documents/inbox-summary", StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll(".view-inbox-card").Count);

        await cut.InvokeAsync(() => cut.FindAll(".view-inbox-card__file")[0].Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".view-inbox-card")));
        using var body = JsonDocument.Parse(_handler.LastFileBody!);
        Assert.True(body.RootElement.GetProperty("accept_suggestions").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("correspondent", out var correspondent) && correspondent.ValueKind != JsonValueKind.Null);
        Assert.EndsWith($"/view/items/{FirstId:D}/inbox/file", _handler.LastFilePath, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("Inbox (1 to file)", cut.Find(".view-documents-rail").TextContent, StringComparison.Ordinal));
        Assert.True(_handler.Requests.Count(request => request.StartsWith("/view/documents/inbox-summary", StringComparison.Ordinal)) > summaries);
        _toasts.WaitForAssertion(() => Assert.Contains("Filed.", _toasts.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileWithoutChangesAcceptsNothing()
    {
        var cut = RenderInbox();

        await cut.InvokeAsync(() => cut.FindAll(".view-inbox-card__file-plain")[0].Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".view-inbox-card")));
        using var body = JsonDocument.Parse(_handler.LastFileBody!);
        Assert.False(body.RootElement.GetProperty("accept_suggestions").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task EnterOnTheFocusedCardFilesItOnADesktop()
    {
        var cut = RenderInbox();
        var summary = cut.FindAll(".view-inbox-card__summary")[0];
        Assert.Equal("0", summary.GetAttribute("tabindex"));

        await cut.InvokeAsync(() => summary.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" }));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".view-inbox-card")));
        using var body = JsonDocument.Parse(_handler.LastFileBody!);
        Assert.True(body.RootElement.GetProperty("accept_suggestions").GetBoolean());
        Assert.Equal("City Water", body.RootElement.GetProperty("suggestions").GetProperty("correspondent").GetString());
    }

    [Fact]
    public async Task AnEditedChipIsSentAsAnExplicitValueWithTheFileButton()
    {
        var cut = RenderInbox();

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Correspondent City Water, change']").Click());
        _dialogs.WaitForAssertion(() => Assert.NotEmpty(_dialogs.FindAll("input")));
        Assert.Equal("City Water", _dialogs.Find("input").GetAttribute("value"));
        _dialogs.Find("input").Input("Acme Water");
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Last(button => button.TextContent.Trim() == "Done").Click());

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".view-inbox-card")[0].QuerySelectorAll(".view-inbox-chip"), chip => chip.TextContent.Trim() == "Acme Water"));
        Assert.DoesNotContain(cut.FindAll(".view-inbox-card")[0].QuerySelectorAll(".view-inbox-chip"), chip => chip.TextContent.Trim() == "City Water");
        await cut.InvokeAsync(() => cut.FindAll(".view-inbox-card__file")[0].Click());

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastFileBody));
        using var body = JsonDocument.Parse(_handler.LastFileBody!);
        Assert.True(body.RootElement.GetProperty("accept_suggestions").GetBoolean());
        Assert.Equal("Acme Water", body.RootElement.GetProperty("correspondent").GetString());
    }

    [Fact]
    public async Task TappingATagChipLeavesItOutAndAddingATagListsTheFinalTags()
    {
        var cut = RenderInbox();

        await cut.InvokeAsync(() => cut.Find("button[aria-label^='Tag Home']").Click());
        Assert.Equal("false", cut.Find("button[aria-label^='Tag Home']").GetAttribute("aria-pressed"));
        Assert.Contains("is-off", cut.Find("button[aria-label^='Tag Home']").ClassName, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Add a tag']").Click());
        _dialogs.WaitForAssertion(() => Assert.NotEmpty(_dialogs.FindAll("input")));
        _dialogs.Find("input").Input("Garden");
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Last(button => button.TextContent.Trim() == "Done").Click());
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".view-inbox-chip"), chip => chip.TextContent.Trim() == "Garden"));

        await cut.InvokeAsync(() => cut.FindAll(".view-inbox-card__file")[0].Click());

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastFileBody));
        using var body = JsonDocument.Parse(_handler.LastFileBody!);
        // File sends exactly what the card shows, so a suggestion that arrives later is never filed unseen.
        Assert.True(body.RootElement.GetProperty("accept_suggestions").GetBoolean());
        var shown = body.RootElement.GetProperty("suggestions");
        Assert.Equal(["Utilities", "Garden"], shown.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray());
        Assert.Equal("City Water", shown.GetProperty("correspondent").GetString());
        Assert.Equal("Invoice", shown.GetProperty("document_type").GetString());
        Assert.True(body.RootElement.GetProperty("correspondent").ValueKind == JsonValueKind.Null, "nothing was typed, so nothing is sent as the person's own choice");
    }

    [Fact]
    public async Task ARejectedFilingKeepsTheCardAndShowsTheEnginesReason()
    {
        _handler.FileStatus = HttpStatusCode.Conflict;
        _handler.FileJson = "{\"title\":\"Invalid request.\",\"detail\":\"That document was already filed.\"}";
        var cut = RenderInbox();

        await cut.InvokeAsync(() => cut.FindAll(".view-inbox-card__file")[0].Click());

        _toasts.WaitForAssertion(() => Assert.Contains("That document was already filed.", _toasts.Markup, StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll(".view-inbox-card").Count);
        Assert.DoesNotContain("Filed.", _toasts.Markup, StringComparison.Ordinal);
        Assert.False(cut.FindAll(".view-inbox-card__file")[0].HasAttribute("disabled"));
    }

    [Fact]
    public void AnEmptyInboxSaysSoAndOffersAllDocuments()
    {
        _handler.Inbox = "{\"items\":[],\"next_cursor\":null,\"has_more\":false}";
        _handler.InboxCount = 0;
        var cut = RenderInbox(waitForCards: false);

        cut.WaitForAssertion(() => Assert.Contains("Your Inbox is empty", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("New documents land here so you can file them in one tap.", cut.Markup, StringComparison.Ordinal);

        cut.Find(".view-documents-empty button").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("filing=inbox", _handler.Requests.Last(value => value.StartsWith("/view/assets", StringComparison.Ordinal)), StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll(".view-documents-rail [role=radio]")[1].GetAttribute("aria-checked")));
    }

    [Fact]
    public void AnotherScopeShowsTheSameCardsReadOnly()
    {
        _handler.ScopeKind = 0;
        var cut = RenderInbox();

        Assert.Equal(2, cut.FindAll(".view-inbox-card").Count);
        Assert.Contains("City Water", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".view-inbox-card__file"));
        Assert.Empty(cut.FindAll(".view-inbox-card__file-plain"));
        Assert.Empty(cut.FindAll("button.view-inbox-chip"));
        Assert.Empty(cut.FindAll(".view-inbox-card__summary[tabindex]"));
    }

    [Fact]
    public async Task APhoneShowsTheFilingActionsAndPicksTheInboxFromViewOptions()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var cut = Render<ViewDocumentsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-documents-sheet-button")));
        Assert.Empty(cut.FindAll(".view-documents-rail"));

        cut.Find(".view-documents-sheet-button").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=radio]")));
        cut.FindAll("[role=radio]")[0].Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".view-inbox-card").Count));
        Assert.Equal(2, cut.FindAll(".view-inbox-card__file").Count);
        Assert.NotEmpty(cut.FindAll("button.view-inbox-chip"));
        // The summary is not a keyboard stop on a phone: Enter-to-file is for desktops.
        Assert.Empty(cut.FindAll(".view-inbox-card__summary[tabindex]"));
    }

    [Fact]
    public async Task ATelevisionShowsCardsAndChipsButNoFilingActions()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync(new DeviceContextSnapshot("television", "tv", false));
        var cut = RenderInbox();

        Assert.Equal(2, cut.FindAll(".view-inbox-card").Count);
        Assert.Empty(cut.FindAll(".view-inbox-card__file"));
        Assert.Empty(cut.FindAll(".view-inbox-card__file-plain"));
        Assert.Empty(cut.FindAll("button.view-inbox-chip"));
        Assert.Contains("City Water", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentsTabShowsTheInboxCountOnlyWhenItIsAboveZero()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/view/documents");
        _handler.InboxCount = 12;
        var waiting = Render<ViewSectionShell>(parameters => parameters.AddChildContent("<p>content</p>"));

        waiting.WaitForAssertion(() => Assert.Equal("12", waiting.Find("#media-section-nav-view-documents .media-section-shell__rail-meta").TextContent.Trim()));
        Assert.Equal("12 documents to file", waiting.Find("#media-section-nav-view-documents .media-section-shell__rail-meta").GetAttribute("aria-label"));
        // The badge asks for the person's own Inbox, whatever scope the page is looking at.
        Assert.Contains(_handler.Requests, request => request.StartsWith("/view/documents/inbox-summary", StringComparison.Ordinal) && request.Contains("scope=mine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDocumentsTabShowsNoBadgeForAnEmptyInbox()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/view/documents");
        _handler.InboxCount = 0;
        var empty = Render<ViewSectionShell>(parameters => parameters.AddChildContent("<p>content</p>"));

        empty.WaitForAssertion(() => Assert.Equal("Documents", empty.Find("#media-section-nav-view-documents").TextContent.Trim()));
        Assert.Empty(empty.FindAll("#media-section-nav-view-documents .media-section-shell__rail-meta"));
    }

    [Fact]
    public void TheInboxIsAModeNotAFilter()
    {
        var state = new ViewDocumentFilterState { InboxOnly = true };

        Assert.False(state.IsActive);
        Assert.Equal(0, state.ActiveCount);
        Assert.Equal(ViewDocumentFilingStates.Inbox, state.ToDto()!.FilingState);
        state.TagIds.Add(Guid.NewGuid());
        state.Clear();
        Assert.True(state.InboxOnly);
        state.InboxOnly = false;
        Assert.Null(state.ToDto());
    }

    private IRenderedComponent<ViewDocumentsPage> RenderInbox(bool waitForCards = true)
    {
        var cut = Render<ViewDocumentsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-documents-rail [role=radio]")));
        cut.FindAll(".view-documents-rail [role=radio]")[0].Click();
        if (waitForCards)
        {
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-inbox-card")));
        }

        return cut;
    }

    private static string InboxItem(Guid id, string title, string extra) =>
        "{\"id\":\"" + id + "\",\"library_id\":\"" + LibraryId + "\",\"media_kind\":\"document\",\"title\":\"" + title
        + "\",\"file_name\":\"bill.pdf\",\"mime_type\":\"application/pdf\",\"created_at\":\"2026-10-01T10:00:00+00:00\",\"captured_at\":null,"
        + "\"source_count\":1,\"files\":[],\"tags\":[],\"thumbnail_url\":\"/t\",\"content_url\":\"/c\",\"favorite\":false,\"hidden\":false,"
        + "\"snippet\":null,\"document_info\":{\"text_source\":\"embedded\",\"text_truncated\":false,\"encrypted\":false,\"has_text\":true,"
        + "\"filing_state\":\"inbox\"," + extra + "}}";

    private sealed class InboxHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int InboxCount { get; set; } = 2;
        public int ScopeKind { get; set; } = 1;
        public HttpStatusCode FileStatus { get; set; } = HttpStatusCode.OK;
        public string FileJson { get; set; } = "{\"text_source\":\"embedded\",\"text_truncated\":false,\"encrypted\":false,\"has_text\":true,\"filing_state\":\"filed\"}";
        public string? LastFileBody { get; private set; }
        public string? LastFilePath { get; private set; }

        public string Inbox { get; set; } = "{\"items\":[" + InboxItem(FirstId, "Water bill", Suggestions) + "," + InboxItem(SecondId, "Power bill", Suggestions)
            + "],\"next_cursor\":null,\"has_more\":false}";

        public string All { get; set; } = "{\"items\":[],\"next_cursor\":null,\"has_more\":false}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            var status = HttpStatusCode.OK;
            string json;
            if (path.EndsWith("/inbox/file", StringComparison.Ordinal))
            {
                LastFilePath = path;
                LastFileBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                status = FileStatus;
                json = FileJson;
                if (FileStatus == HttpStatusCode.OK)
                {
                    InboxCount = Math.Max(0, InboxCount - 1);
                    var id = path.Split('/')[3];
                    Inbox = RemoveItem(Inbox, id);
                }
            }
            else
            {
                json = path switch
                {
                    "/profiles" => "[]",
                    "/view/preferences" => "{\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"scope\":" + ScopeKind + ",\"scope_profile_id\":null,\"timeline_density\":1,\"viewer_info_open\":true,\"updated_at\":null}",
                    "/view/scopes" => "{\"scope\":{\"kind\":" + ScopeKind + ",\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"was_fallback\":false},\"available_scopes\":[{\"kind\":" + ScopeKind + ",\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"label\":\"Mine\",\"avatar_color\":null,\"avatar_url\":null}],\"can_browse_other_people\":false,\"can_view_documents\":true}",
                    "/view/documents/inbox-summary" => "{\"count\":" + InboxCount + "}",
                    "/view/assets" => request.RequestUri.Query.Contains("filing=inbox", StringComparison.Ordinal) ? Inbox : All,
                    "/view/catalogue" => "{\"tags\":[{\"id\":\"aaaaaaaa-0000-0000-0000-000000000001\",\"name\":\"Tax\",\"colour\":null,\"usage_count\":3,\"can_manage\":true}],"
                        + "\"correspondents\":[{\"id\":\"bbbbbbbb-0000-0000-0000-000000000001\",\"name\":\"Acme\",\"colour\":null,\"usage_count\":2,\"can_manage\":true}],\"document_types\":[],\"fields\":[]}",
                    _ => "{\"owned\":[],\"shared_with_you\":[]}",
                };
            }

            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static string RemoveItem(string page, string id)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(page)!.AsObject();
            var items = node["items"]!.AsArray();
            var kept = items.Where(item => !string.Equals(item!["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                .Select(item => System.Text.Json.Nodes.JsonNode.Parse(item!.ToJsonString())).ToList();
            node["items"] = new System.Text.Json.Nodes.JsonArray(kept.ToArray());
            return node.ToJsonString();
        }
    }
}
