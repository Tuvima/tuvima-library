using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class ViewDocumentOrganisationUiTests : AsyncBunitContext
{
    private static readonly Guid TaxTag = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UnusedTag = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Acme = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Invoice = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid StatusField = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid AmountField = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    private static readonly string Catalogue = """
        {"tags":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","name":"Tax","colour":null,"usage_count":3,"can_manage":true},
                 {"id":"aaaaaaaa-0000-0000-0000-000000000002","name":"Unused","colour":null,"usage_count":0,"can_manage":true}],
         "correspondents":[{"id":"bbbbbbbb-0000-0000-0000-000000000001","name":"Acme","colour":null,"usage_count":2,"can_manage":true}],
         "document_types":[{"id":"cccccccc-0000-0000-0000-000000000001","name":"Invoice","usage_count":2,"can_manage":false}],
         "fields":[{"id":"dddddddd-0000-0000-0000-000000000001","name":"Status","kind":"choice","choices":["Open","Closed"],"usage_count":1,"can_manage":true},
                   {"id":"dddddddd-0000-0000-0000-000000000002","name":"Amount","kind":"money","choices":[],"usage_count":1,"can_manage":true}]}
        """;

    private readonly RoutedHandler _handler = new();
    private readonly EngineApiClient _api;

    public ViewDocumentOrganisationUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        _api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Services.AddSingleton<IEngineApiClient>(_api);
    }

    [Fact]
    public void TheFilterRailShowsCountsHidesUnusedNamesAndNarrowsOnEveryChange()
    {
        var state = new ViewDocumentFilterState();
        var changes = 0;
        var cut = Render<ViewDocumentFilterRail>(parameters => parameters
            .Add(rail => rail.Catalogue, JsonSerializer.Deserialize<ViewDocumentCatalogueDto>(Catalogue))
            .Add(rail => rail.State, state)
            .Add(rail => rail.OnChanged, EventCallback.Factory.Create(this, () => changes++)));

        Assert.Contains("Tax (3)", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Unused", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Acme (2)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Invoice (2)", cut.Markup, StringComparison.Ordinal);
        // Only choice and yes/no fields are offered as filters.
        Assert.Contains("Status", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">Amount<", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear all", cut.Markup, StringComparison.Ordinal);

        cut.Find("input[type=checkbox]").Change(true);
        Assert.Contains(TaxTag, state.TagIds);
        cut.Find("select[aria-label='Filter by correspondent']").Change(Acme.ToString("D"));
        Assert.Equal(Acme, state.CorrespondentId);
        cut.Find("select[aria-label='Filter by document type']").Change(Invoice.ToString("D"));
        Assert.Equal(Invoice, state.DocumentTypeId);
        cut.Find("select[aria-label='Filter by Status']").Change("Open");
        Assert.Equal("Open", state.Fields[StatusField]);
        cut.Find("input[aria-label='Documents dated from']").Change("2026-01-15");
        cut.Find("input[aria-label='Documents dated up to']").Change("2026-02-01");
        Assert.Equal((new DateOnly(2026, 1, 15), new DateOnly(2026, 2, 1)), (state.DateFrom, state.DateTo));
        Assert.Equal(6, changes);

        var filter = state.ToDto()!;
        Assert.Equal([TaxTag], filter.TagIds);
        Assert.Equal(Acme, filter.CorrespondentId);
        Assert.Equal("Open", Assert.Single(filter.Fields!).Value);

        cut.Find("button.view-documents-filters__clear").Click();
        Assert.False(state.IsActive);
        Assert.Null(state.ToDto());
        Assert.Equal(7, changes);
    }

    [Fact]
    public void ASelectedNameStaysListedEvenWhenNothingUsesItAnymore()
    {
        var state = new ViewDocumentFilterState();
        state.TagIds.Add(UnusedTag);
        var cut = Render<ViewDocumentFilterRail>(parameters => parameters
            .Add(rail => rail.Catalogue, JsonSerializer.Deserialize<ViewDocumentCatalogueDto>(Catalogue))
            .Add(rail => rail.State, state));

        Assert.Contains("Unused (0)", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditorOffersEveryDetailAndSavesThemInOneRequest()
    {
        _handler.Set("GET", "/view/items/{id}/document-details", Info());
        _handler.Set("GET", "/view/catalogue", Catalogue);
        _handler.Set("PUT", "/view/items/{id}/document-details", Info());
        var dialog = new DialogSpy();
        var cut = RenderDialog(dialog);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[aria-label='Document date']")));
        cut.Find("input[aria-label='Document date']").Change("2026-03-01");
        cut.Find("textarea").Input("Pay by April");
        cut.Find("input[aria-label='Tag to add']").Input("Utilities");
        cut.Find("form.app-tag-input").Submit();
        cut.Find("select[aria-label='Status']").Change("Open");
        cut.Find("input[aria-label='Amount amount']").Change("120.50");
        cut.Find("input[aria-label='Amount currency']").Change("eur");
        var comboboxes = cut.FindAll("input[role=combobox]");
        comboboxes[0].Input("City Water");
        comboboxes[1].Input("Bill");
        cut.Find(".app-dialog-shell__footer button.app-button--filled, .app-dialog-shell__footer button:last-child").Click();

        cut.WaitForAssertion(() => Assert.NotNull(dialog.Result));
        Assert.False(dialog.Result!.Canceled);
        using var body = JsonDocument.Parse(_handler.LastBody("PUT", "/view/items/{id}/document-details")!);
        var root = body.RootElement;
        Assert.Equal("2026-03-01", root.GetProperty("document_date").GetString());
        Assert.Equal("City Water", root.GetProperty("correspondent").GetString());
        Assert.Equal("Bill", root.GetProperty("document_type").GetString());
        Assert.Equal("Pay by April", root.GetProperty("notes").GetString());
        Assert.Contains("Utilities", root.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
        var values = root.GetProperty("field_values").EnumerateArray().ToList();
        Assert.Equal("Open", values.Single(value => value.GetProperty("field_id").GetGuid() == StatusField).GetProperty("text_value").GetString());
        var money = values.Single(value => value.GetProperty("field_id").GetGuid() == AmountField);
        Assert.Equal(120.5, money.GetProperty("number_value").GetDouble());
        Assert.Equal("EUR", money.GetProperty("text_value").GetString());
    }

    [Fact]
    public void SavingWhenCustomFieldsCouldNotBeLoadedLeavesThemUntouched()
    {
        _handler.Set("GET", "/view/items/{id}/document-details", Info());
        _handler.Set("GET", "/view/catalogue", "not json", HttpStatusCode.InternalServerError);
        _handler.Set("PUT", "/view/items/{id}/document-details", Info());
        var dialog = new DialogSpy();
        var cut = RenderDialog(dialog);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[aria-label='Document date']")));
        Assert.Contains("custom fields stay as they are", cut.Markup, StringComparison.Ordinal);
        cut.Find(".app-dialog-shell__footer button:last-child").Click();

        cut.WaitForAssertion(() => Assert.NotNull(dialog.Result));
        using var body = JsonDocument.Parse(_handler.LastBody("PUT", "/view/items/{id}/document-details")!);
        Assert.False(body.RootElement.TryGetProperty("field_values", out var values) && values.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void ARejectedSaveStaysOpenAndShowsTheEnginesReason()
    {
        _handler.Set("GET", "/view/items/{id}/document-details", Info());
        _handler.Set("GET", "/view/catalogue", Catalogue);
        _handler.Set("PUT", "/view/items/{id}/document-details", """{"title":"Invalid request.","detail":"A tag named 'x' already exists."}""", HttpStatusCode.Conflict);
        var dialog = new DialogSpy();
        var cut = RenderDialog(dialog);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[aria-label='Document date']")));
        cut.Find(".app-dialog-shell__footer button:last-child").Click();

        cut.WaitForAssertion(() => Assert.Contains("already exists", cut.Markup, StringComparison.Ordinal));
        Assert.Null(dialog.Result);
    }

    [Fact]
    public void ABadNumberIsExplainedBeforeAnythingIsSent()
    {
        _handler.Set("GET", "/view/items/{id}/document-details", Info());
        _handler.Set("GET", "/view/catalogue", Catalogue);
        var dialog = new DialogSpy();
        var cut = RenderDialog(dialog);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[aria-label='Amount amount']")));
        cut.Find("input[aria-label='Amount amount']").Change("abc");
        cut.Find(".app-dialog-shell__footer button:last-child").Click();

        cut.WaitForAssertion(() => Assert.Contains("as a number", cut.Markup, StringComparison.Ordinal));
        Assert.Null(_handler.LastBody("PUT", "/view/items/{id}/document-details"));
        Assert.Null(dialog.Result);
    }

    [Fact]
    public void TheSettingsListsShowOnlyWhatThePersonMayChange()
    {
        _handler.Set("GET", "/view/catalogue", Catalogue);
        var cut = Render<DocumentOrganisationSettings>();

        cut.WaitForAssertion(() => Assert.Contains("Acme", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("3 items", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("button[aria-label='Rename Tax']"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Remove Acme']"));
        // Invoice is somebody else's: it is listed but cannot be renamed or removed here.
        Assert.Contains("Invoice", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button[aria-label='Rename Invoice']"));
        Assert.Empty(cut.FindAll("button[aria-label='Remove Invoice']"));
        Assert.Contains("Status", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Open, Closed", cut.Markup, StringComparison.Ordinal);
    }

    private IRenderedComponent<ViewDocumentDetailsDialog> RenderDialog(DialogSpy dialog) =>
        Render<ViewDocumentDetailsDialog>(parameters => parameters
            .Add(component => component.Item, Document())
            .AddCascadingValue<IAppDialogContext>(dialog));

    private static LocalAssetDto Document() => JsonSerializer.Deserialize<LocalAssetDto>("""
        {"id":"11111111-1111-1111-1111-111111111111","library_id":"22222222-2222-2222-2222-222222222222","media_kind":"document",
         "title":"Water bill","file_name":"bill.pdf","mime_type":"application/pdf","created_at":"2026-03-02T10:00:00+00:00","captured_at":null,
         "source_count":1,"files":[],"tags":["Tax"],"thumbnail_url":"/t","content_url":"/c","favorite":false,"hidden":false,"document_info":null}
        """)!;

    private static string Info() => """
        {"text_source":"embedded","text_truncated":false,"encrypted":false,"has_text":true,"filing_state":"inbox",
         "tags":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","name":"Tax"}],"field_values":[]}
        """;

    private sealed class DialogSpy : IAppDialogContext
    {
        public AppDialogResult? Result { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public AppDialogOptions Options { get; } = new();
        public string? PresentationClass => null;
        public void Close(AppDialogResult? result = null) => Result = result ?? AppDialogResult.Ok(new object());
        public void Cancel() => Result = AppDialogResult.Cancel();
        public Task CloseAsync(AppDialogResult? result = null) { Close(result); return Task.CompletedTask; }
        public Task CancelAsync() { Cancel(); return Task.CompletedTask; }
        public Task SetOptionsAsync(AppDialogOptions options) => Task.CompletedTask;
        public Task SetPresentationClassAsync(string? value) => Task.CompletedTask;
        public void SetCloseGuard(Func<Task<bool>>? guard) { }
        public IDisposable RegisterCloseGuard(Func<Task<bool>> guard) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private sealed class RoutedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Json, HttpStatusCode Status)> _routes = [];
        private readonly Dictionary<string, string> _bodies = [];

        public void Set(string method, string route, string json, HttpStatusCode status = HttpStatusCode.OK) =>
            _routes[$"{method} {route}"] = (json, status);

        public string? LastBody(string method, string route) => _bodies.GetValueOrDefault($"{method} {route}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var key = $"{request.Method} {System.Text.RegularExpressions.Regex.Replace(path, "/items/[0-9a-fA-F-]{36}", "/items/{id}")}";
            if (request.Content is not null)
            {
                _bodies[key] = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var (json, status) = _routes.TryGetValue(key, out var found) ? found : ("{}", HttpStatusCode.OK);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
