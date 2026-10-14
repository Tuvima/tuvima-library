using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>Saved views, select mode and the bulk edit, the sort and the CSV export on the Documents page.</summary>
public sealed class ViewDocumentBulkUiTests : AsyncBunitContext
{
    private static readonly Guid LibraryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid[] ItemIds =
    [
        Guid.Parse("31111111-1111-1111-1111-111111111111"),
        Guid.Parse("32222222-1111-1111-1111-111111111111"),
        Guid.Parse("33333333-1111-1111-1111-111111111111"),
    ];

    private static readonly Guid SavedViewId = Guid.Parse("41111111-1111-1111-1111-111111111111");

    private readonly Handler _handler = new();
    private readonly DeviceContextService _device;
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private readonly IRenderedComponent<AppToastHost> _toasts;

    public ViewDocumentBulkUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        _device = new DeviceContextService(api);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddScoped<ActiveProfileAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)));
        _dialogs = Render<AppDialogHost>();
        _toasts = Render<AppToastHost>();
    }

    private IRenderedComponent<ViewDocumentsPage> RenderLoaded()
    {
        var cut = Render<ViewDocumentsPage>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".view-document-card").Count));
        return cut;
    }

    private static void EnterSelectMode(IRenderedComponent<ViewDocumentsPage> cut) =>
        cut.Find("button.view-documents-select").Click();

    [Fact]
    public void SelectModeShowsACheckboxPerCardAndTheBulkBarAppearsOnceSomethingIsPicked()
    {
        var cut = RenderLoaded();
        Assert.Empty(cut.FindAll(".view-selection-toolbar"));

        EnterSelectMode(cut);
        Assert.Equal(3, cut.FindAll(".view-document-card__select").Count);
        cut.FindAll(".view-document-card__select input")[0].Change(true);

        var bar = cut.Find(".view-selection-toolbar");
        Assert.Equal("Selected document actions", bar.GetAttribute("aria-label"));
        Assert.Contains("1 selected", bar.TextContent, StringComparison.Ordinal);
        Assert.Contains("Organise", bar.TextContent, StringComparison.Ordinal);
        Assert.Contains("Trash", bar.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[style]"));
    }

    [Fact]
    public void ShiftAndClickSelectsEveryDocumentBetweenTheLastPickAndThisOne()
    {
        var cut = RenderLoaded();
        EnterSelectMode(cut);
        cut.FindAll(".view-document-card__select input")[0].Change(true);

        cut.FindAll(".view-document-card__open")[2].Click(new MouseEventArgs { ShiftKey = true });

        Assert.Contains("3 selected", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal);
        Assert.Equal(3, cut.FindAll(".view-document-card.is-selected").Count);
    }

    [Fact]
    public void TheTapAfterSelectModeStartsTogglesInsteadOfOpeningTheDocument()
    {
        var cut = RenderLoaded();
        EnterSelectMode(cut);

        cut.FindAll(".view-document-card__open")[1].Click();

        Assert.Contains("1 selected", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".view-document-viewer"));
    }

    [Fact]
    public void MovingToTheTrashSendsOneBulkCallAndSaysPlainlyWhatHappened()
    {
        var cut = RenderLoaded();
        EnterSelectMode(cut);
        cut.FindAll(".view-document-card__select input")[0].Change(true);
        cut.FindAll(".view-document-card__select input")[1].Change(true);

        cut.FindAll(".view-selection-toolbar__action").Single(button => button.TextContent.Contains("Trash", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastBulkBody));
        using var body = JsonDocument.Parse(_handler.LastBulkBody!);
        Assert.Equal(ViewDocumentBulkOperations.MoveToTrash, body.RootElement.GetProperty("operation").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("item_ids").GetArrayLength());
        cut.WaitForAssertion(() => Assert.Contains("Updated all 2 documents.", cut.Find(".view-documents-bulk-report").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".view-selection-toolbar"));
    }

    [Fact]
    public void APartialResultNamesTheDocumentsThatWereLeftAloneAndWhy()
    {
        _handler.BulkResponse = "{\"operation\":\"move_to_trash\",\"requested\":3,\"done\":1,\"skipped\":2,\"failed\":0,\"items\":["
            + $"{{\"item_id\":\"{ItemIds[0]}\",\"status\":\"done\"}},"
            + $"{{\"item_id\":\"{ItemIds[1]}\",\"status\":\"skipped\",\"reason\":\"In the Shared Library: only filing rules can change it.\"}},"
            + $"{{\"item_id\":\"{ItemIds[2]}\",\"status\":\"skipped\",\"reason\":\"In the Shared Library: only filing rules can change it.\"}}]}}";
        var cut = RenderLoaded();
        EnterSelectMode(cut);
        foreach (var box in cut.FindAll(".view-document-card__select input"))
        {
            box.Change(true);
        }

        cut.FindAll(".view-selection-toolbar__action").Single(button => button.TextContent.Contains("Trash", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Updated 1 of 3 documents.", cut.Find(".view-documents-bulk-report").TextContent, StringComparison.Ordinal));
        var reasons = cut.FindAll(".view-documents-bulk-report__reasons li").Select(item => item.TextContent.Trim()).ToList();
        Assert.Equal(["2 left as they were. In the Shared Library: only filing rules can change it."], reasons);
    }

    [Fact]
    public void AddingTagsAsksWhichTagsFirstAndThenSendsThemByName()
    {
        var cut = RenderLoaded();
        EnterSelectMode(cut);
        cut.FindAll(".view-document-card__select input")[0].Change(true);

        cut.Find(".view-selection-toolbar button[aria-haspopup=menu]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=menuitem]")));
        cut.FindAll("[role=menuitem]").Single(item => item.TextContent.Contains("Add tags", StringComparison.Ordinal)).Click();

        _dialogs.WaitForAssertion(() => Assert.Contains("Add tags", _dialogs.Markup, StringComparison.Ordinal));
        _dialogs.Find("input[aria-label='Tag to add']").Input("Receipts");
        _dialogs.Find("form.app-tag-input").Submit();
        _dialogs.FindAll("button").Single(button => button.TextContent.Contains("Apply to 1 document", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastBulkBody));
        using var body = JsonDocument.Parse(_handler.LastBulkBody!);
        Assert.Equal(ViewDocumentBulkOperations.AddTags, body.RootElement.GetProperty("operation").GetString());
        Assert.Equal("Receipts", body.RootElement.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public void SavingTheCurrentFiltersMakesASmartGalleryThatStaysRelative()
    {
        var cut = RenderLoaded();
        cut.WaitForAssertion(() => Assert.Contains("Tax (3)", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".view-documents-saved__save"));

        cut.Find("input[type=checkbox]").Change(true);
        cut.Find("select[aria-label='Document date range']").Change("last_30_days");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-documents-saved__save")));
        cut.Find(".view-documents-saved__save").Click();
        cut.Find("input[aria-label='Name for this view']").Input("Tax this month");
        cut.Find(".view-documents-saved__form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastGalleryBody));
        using var body = JsonDocument.Parse(_handler.LastGalleryBody!);
        Assert.Equal("Tax this month", body.RootElement.GetProperty("name").GetString());
        Assert.Equal((int)ViewGalleryKind.Smart, body.RootElement.GetProperty("kind").GetInt32());
        var rule = body.RootElement.GetProperty("smart_rule_json").GetString()!;
        Assert.True(ViewSmartGalleryRules.IsDocumentView(rule));
        var conditions = ViewSmartGalleryRules.Parse(rule).AllConditions;
        Assert.Contains(conditions, condition => condition is { Field: "tags", Op: "all" });
        Assert.Contains(conditions, condition => condition is { Field: "document_date", Op: "within", Value: "last_30_days" });
        Assert.DoesNotContain(conditions, condition => condition.Field == "document_date" && condition.Op is "between" or "gte" or "lte");
    }

    [Fact]
    public void ASavedViewSitsAtTheTopOfTheRailAndStartsTheListFromIt()
    {
        _handler.Galleries = "{\"owned\":[" + SavedViewJson("Recent invoices") + "," + ManualGalleryJson("Holiday") + "],\"shared_with_you\":[]}";
        var cut = RenderLoaded();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".view-documents-saved__view")));
        var rail = cut.Find(".view-documents-rail").FirstElementChild!.FirstElementChild!;
        Assert.Contains("view-documents-saved", rail.ClassName, StringComparison.Ordinal);
        Assert.DoesNotContain("Holiday", rail.TextContent, StringComparison.Ordinal);
        Assert.Contains("Recent invoices", rail.TextContent, StringComparison.Ordinal);

        cut.Find(".view-documents-saved__view").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.StartsWith("/view/assets", StringComparison.Ordinal) && request.Contains($"galleryId={SavedViewId:D}", StringComparison.Ordinal)));
        Assert.Equal("true", cut.Find(".view-documents-saved__view").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void RenamingASavedViewKeepsItsRule()
    {
        _handler.Galleries = "{\"owned\":[" + SavedViewJson("Recent invoices") + "],\"shared_with_you\":[]}";
        var cut = RenderLoaded();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".view-documents-saved__view")));

        cut.Find(".view-documents-saved__menu button[aria-haspopup=menu]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=menuitem]")));
        cut.FindAll("[role=menuitem]").Single(item => item.TextContent.Contains("Rename", StringComparison.Ordinal)).Click();
        cut.WaitForElement("input[aria-label='New name for Recent invoices']").Input("Fresh invoices");
        cut.Find(".view-documents-saved__form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastGalleryBody));
        using var body = JsonDocument.Parse(_handler.LastGalleryBody!);
        Assert.Equal("Fresh invoices", body.RootElement.GetProperty("name").GetString());
        Assert.True(ViewSmartGalleryRules.IsDocumentView(body.RootElement.GetProperty("smart_rule_json").GetString()));
        Assert.Equal("PUT", _handler.LastGalleryMethod);
    }

    [Fact]
    public void ChoosingASortReloadsTheListAndRemembersItForTheProfile()
    {
        var cut = RenderLoaded();

        cut.Find("select[aria-label='Sort documents']").Change(ViewDocumentSorts.Correspondent);

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.StartsWith("/view/assets", StringComparison.Ordinal) && request.Contains("docSort=correspondent", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Equal("{\"sort\":\"correspondent\"}", _handler.LastSortBody));
    }

    [Fact]
    public void TheRememberedSortIsUsedForTheFirstListOfAVisit()
    {
        _handler.RememberedSort = ViewDocumentSorts.Title;
        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.StartsWith("/view/assets", StringComparison.Ordinal) && request.Contains("docSort=title", StringComparison.Ordinal)));
        Assert.Equal(ViewDocumentSorts.Title, cut.Find("select[aria-label='Sort documents']").GetAttribute("value"));
    }

    [Fact]
    public void ExportingSendsTheCurrentFiltersDownloadsTheFileAndWarnsWhenTheListWasCut()
    {
        _handler.ExportTruncated = true;
        var cut = RenderLoaded();
        cut.Find("select[aria-label='Document date range']").Change("this_year");

        cut.Find("button.view-documents-export").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.StartsWith("/view/documents/export", StringComparison.Ordinal)));
        var export = _handler.Requests.Last(request => request.StartsWith("/view/documents/export", StringComparison.Ordinal));
        Assert.Contains("docFrom=", export, StringComparison.Ordinal);
        Assert.Contains("docTo=", export, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, call => call.Identifier == "tuvimaDownloads.fromStream"));
        var download = JSInterop.Invocations.Single(call => call.Identifier == "tuvimaDownloads.fromStream");
        Assert.Equal("documents-2026-10-14.csv", download.Arguments[0]);
        Assert.Equal("text/csv;charset=utf-8", download.Arguments[2]);
        _toasts.WaitForAssertion(() => Assert.Contains("Only the first 10,000 documents were exported.", _toasts.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnAPhonePressingAndHoldingADocumentStartsSelectingAndTheNextTapDoesNotOpenIt()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var cut = RenderLoaded();
        Assert.Empty(cut.FindAll("button.view-documents-select"));

        // A quick tap that is released before the hold finishes is just a tap: nothing is selected.
        var first = cut.FindAll("li.view-document-card")[0];
        first.TriggerEvent("onpointerdown", new PointerEventArgs());
        first.TriggerEvent("onpointerup", new PointerEventArgs());
        await Task.Delay(700);
        Assert.Empty(cut.FindAll(".view-selection-toolbar"));

        var held = cut.FindAll("li.view-document-card")[1];
        held.TriggerEvent("onpointerdown", new PointerEventArgs());

        cut.WaitForAssertion(() => Assert.Contains("1 selected", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.NotEmpty(cut.FindAll("button.view-documents-select"));
        Assert.Equal(3, cut.FindAll(".view-document-card__select").Count);
        cut.FindAll(".view-document-card__open")[1].Click();
        Assert.Contains("1 selected", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal);
        cut.FindAll(".view-document-card__open")[2].Click();
        Assert.Contains("2 selected", cut.Find(".view-selection-toolbar").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATelevisionOnlyBrowsesWithNoSelectionSavedViewsOrExport()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("television", "tv", false));
        var cut = RenderLoaded();

        Assert.Empty(cut.FindAll("button.view-documents-select"));
        Assert.Empty(cut.FindAll(".view-documents-saved"));
        Assert.Empty(cut.FindAll("button.view-documents-export"));
    }

    [Fact]
    public void TheTrashViewOffersRestoreInsteadOfOrganising()
    {
        var cut = RenderLoaded();
        cut.FindAll(".view-documents-rail [role=radio]").Single(radio => radio.TextContent.Trim() == "Trash").Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Contains("lifecycle=trashed", StringComparison.Ordinal)));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".view-document-card").Count));

        EnterSelectMode(cut);
        cut.FindAll(".view-document-card__select input")[0].Change(true);

        var bar = cut.Find(".view-selection-toolbar");
        Assert.Contains("Restore", bar.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Organise", bar.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedViewThatIsAppliedCannotBeSavedAgainAsIfItWereEverything()
    {
        var state = new ViewDocumentFilterState { SavedViewId = SavedViewId };
        state.TagIds.Add(Guid.NewGuid());

        Assert.False(ViewDocumentSavedViews.CanSave(state, "words"));
        state.Clear();
        Assert.False(ViewDocumentSavedViews.CanSave(state, null));
        Assert.True(ViewDocumentSavedViews.CanSave(state, "words"));
    }

    [Fact]
    public void TheFilterStateKeepsRelativeRangesRelativeAndResolvesThemOnlyWhenAsking()
    {
        var state = new ViewDocumentFilterState { DatePreset = "last_30_days", Sort = ViewDocumentSorts.Title };

        var dto = state.ToDto(new DateOnly(2026, 10, 14))!;

        Assert.Equal(new DateOnly(2026, 9, 15), dto.DateFrom);
        Assert.Equal(new DateOnly(2026, 10, 14), dto.DateTo);
        Assert.Equal(ViewDocumentSorts.Title, dto.Sort);
        Assert.Equal("last_30_days", state.DatePreset);
        Assert.Equal(1, state.ActiveCount);
        state.Clear();
        Assert.Equal(ViewDocumentSorts.Title, state.Sort);
        Assert.Null(state.DatePreset);
        Assert.NotNull(state.ToDto());
        state.Sort = null;
        Assert.Null(state.ToDto());
    }

    [Fact]
    public void ASavedViewRuleListsEveryFilterAndNeverTheTrashOrAFixedDayForARelativeRange()
    {
        var tag = Guid.NewGuid();
        var field = Guid.NewGuid();
        var state = new ViewDocumentFilterState { CorrespondentId = Guid.NewGuid(), DocumentTypeId = Guid.NewGuid(), InboxOnly = true };
        state.TagIds.Add(tag);
        state.DateFrom = new DateOnly(2026, 1, 1);
        state.DateTo = new DateOnly(2026, 6, 30);
        state.SetField(field, "Open");
        var catalogue = new ViewDocumentCatalogueDto([], [], [], [new ViewDocumentFieldDto(field, "Status", ViewDocumentFieldKinds.Choice, ["Open"], 1, true)]);

        var rule = ViewDocumentSavedViews.BuildRuleJson(state, "electricity", catalogue);

        var conditions = ViewSmartGalleryRules.Parse(rule).AllConditions;
        Assert.Contains(conditions, condition => condition is { Field: "media_type", Op: "eq", Value: "document" });
        Assert.Contains(conditions, condition => condition is { Field: "tags", Op: "all" } && condition.GetEffectiveValues().SequenceEqual([tag.ToString("D")]));
        Assert.Contains(conditions, condition => condition.Field == "correspondent");
        Assert.Contains(conditions, condition => condition.Field == "document_type");
        Assert.Contains(conditions, condition => condition is { Field: "document_date", Op: "between" });
        Assert.Contains(conditions, condition => condition.Field == ViewSmartGalleryRules.CustomFieldKey(field) && condition.Op == "eq");
        Assert.Contains(conditions, condition => condition is { Field: "filing_state", Value: "inbox" });
        Assert.Contains(conditions, condition => condition is { Field: "text_contains", Op: "contains", Value: "electricity" });
        Assert.True(ViewDocumentSavedViews.CanSave(state, null));
        state.TrashOnly = true;
        Assert.False(ViewDocumentSavedViews.CanSave(state, "x"));
    }

    private static string SavedViewJson(string name)
    {
        var rule = ViewSmartGalleryRules.Serialize(MediaEngine.Domain.Models.CollectionRuleDefinition.SingleGroup(
        [
            new MediaEngine.Domain.Models.CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "document" },
            new MediaEngine.Domain.Models.CollectionRulePredicate { Field = "document_date", Op = "within", Value = "last_30_days" },
        ]));
        return GalleryJson(SavedViewId, name, 1, rule);
    }

    private static string ManualGalleryJson(string name) => GalleryJson(Guid.NewGuid(), name, 0, null);

    private static string GalleryJson(Guid id, string name, int kind, string? rule) =>
        $"{{\"id\":\"{id}\",\"owner_profile_id\":\"00000000-0000-0000-0000-000000000001\",\"personal_space_id\":\"00000000-0000-0000-0000-000000000002\","
        + $"\"name\":\"{name}\",\"description\":null,\"kind\":{kind},\"smart_rule_json\":{(rule is null ? "null" : JsonSerializer.Serialize(rule))},\"cover_item_id\":null,"
        + "\"sort_order\":0,\"item_count\":2,\"created_at\":\"2026-10-01T10:00:00+00:00\",\"updated_at\":\"2026-10-01T10:00:00+00:00\"}";

    private static string Item(Guid id, string file, string title) =>
        $"{{\"id\":\"{id}\",\"library_id\":\"{LibraryId}\",\"media_kind\":\"document\",\"title\":\"{title}\",\"file_name\":\"{file}\",\"mime_type\":\"application/pdf\","
        + "\"created_at\":\"2026-10-01T10:00:00+00:00\",\"captured_at\":null,\"source_count\":1,\"files\":[],\"tags\":[\"tax\"],\"thumbnail_url\":\"/t\",\"content_url\":\"/c\","
        + "\"favorite\":false,\"hidden\":false,\"document_info\":{\"text_source\":\"embedded\",\"text_truncated\":false,\"encrypted\":false,\"has_text\":true}}";

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? LastBulkBody { get; private set; }
        public string? LastGalleryBody { get; private set; }
        public string? LastGalleryMethod { get; private set; }
        public string? LastSortBody { get; private set; }
        public string? RememberedSort { get; set; }
        public bool ExportTruncated { get; set; }
        public string Galleries { get; set; } = "{\"owned\":[],\"shared_with_you\":[]}";
        public string? BulkResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            var status = HttpStatusCode.OK;
            string json;
            if (path == "/view/documents/bulk")
            {
                LastBulkBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(LastBulkBody);
                var ids = body.RootElement.GetProperty("item_ids").EnumerateArray().Select(value => value.GetString()!).ToList();
                json = BulkResponse ?? "{\"operation\":\"" + body.RootElement.GetProperty("operation").GetString() + "\",\"requested\":" + ids.Count + ",\"done\":" + ids.Count
                    + ",\"skipped\":0,\"failed\":0,\"items\":[" + string.Join(",", ids.Select(id => $"{{\"item_id\":\"{id}\",\"status\":\"done\"}}")) + "]}";
            }
            else if (path == "/view/documents/export")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Title\r\n")) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
                response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "documents-2026-10-14.csv" };
                if (ExportTruncated)
                {
                    response.Headers.Add("X-Tuvima-Export-Truncated", "true");
                }

                return response;
            }
            else if (path == "/view/preferences/documents-sort")
            {
                if (request.Method == HttpMethod.Put)
                {
                    LastSortBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    json = LastSortBody;
                }
                else
                {
                    json = "{\"sort\":" + (RememberedSort is null ? "null" : "\"" + RememberedSort + "\"") + "}";
                }
            }
            else if (path.StartsWith("/view/galleries", StringComparison.Ordinal) && request.Method != HttpMethod.Get)
            {
                LastGalleryMethod = request.Method.Method;
                LastGalleryBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                json = GalleryJson(SavedViewId, "Saved", 1, null);
                status = request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK;
            }
            else
            {
                json = path switch
                {
                    "/profiles" => "[]",
                    "/view/preferences" => "{\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"scope\":1,\"scope_profile_id\":null,\"timeline_density\":1,\"viewer_info_open\":true,\"updated_at\":null}",
                    "/view/scopes" => "{\"scope\":{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"was_fallback\":false},\"available_scopes\":[{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"label\":\"Mine\",\"avatar_color\":null,\"avatar_url\":null}],\"can_browse_other_people\":false,\"can_view_documents\":true}",
                    "/view/documents/inbox-summary" => "{\"count\":0}",
                    "/view/assets" => "{\"items\":[" + string.Join(",", ItemIds.Select((id, index) => Item(id, $"doc{index}.pdf", $"Document {index}"))) + "],\"next_cursor\":null,\"has_more\":false}",
                    "/view/catalogue" => "{\"tags\":[{\"id\":\"aaaaaaaa-0000-0000-0000-000000000001\",\"name\":\"Tax\",\"colour\":null,\"usage_count\":3,\"can_manage\":true}],"
                        + "\"correspondents\":[],\"document_types\":[],\"fields\":[]}",
                    "/view/galleries" => Galleries,
                    "/view/tag-suggestions" => "[]",
                    _ => "{}",
                };
            }

            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
