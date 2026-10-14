using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class ViewDocumentRulesUiTests : AsyncBunitContext
{
    private static readonly Guid RuleOne = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid RuleTwo = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid BillsTag = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Acme = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid CostField = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid SourceId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");

    private const string Catalogue = """
        {"tags":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","name":"Bills","colour":null,"usage_count":3,"can_manage":true}],
         "correspondents":[{"id":"bbbbbbbb-0000-0000-0000-000000000001","name":"Acme","colour":null,"usage_count":2,"can_manage":true}],
         "document_types":[],
         "fields":[{"id":"dddddddd-0000-0000-0000-000000000002","name":"Cost","kind":"money","choices":[],"usage_count":1,"can_manage":true}]}
        """;

    private const string Rules = """
        {"rules":[
          {"id":"11111111-0000-0000-0000-000000000001","name":"Water bills","enabled":true,"match_mode":"all","sort_order":0,"files_automatically":true,"scope":"mine",
           "conditions":[{"kind":"text_contains","value":"water","display_value":"water"},{"kind":"file_kind","value":"pdf","display_value":"pdf"}],
           "actions":[{"kind":"add_tag","ref_id":"aaaaaaaa-0000-0000-0000-000000000001","display_name":"Bills","missing":false},
                      {"kind":"set_correspondent","ref_id":"bbbbbbbb-0000-0000-0000-000000000001","display_name":"Acme","missing":false}]},
          {"id":"11111111-0000-0000-0000-000000000002","name":"Scans","enabled":false,"match_mode":"any","sort_order":1,"files_automatically":false,"scope":"mine",
           "conditions":[{"kind":"source","value":"ffffffff-0000-0000-0000-000000000001","display_value":"Scanner folder"}],
           "actions":[{"kind":"add_tag","ref_id":"99999999-0000-0000-0000-000000000009","display_name":null,"missing":true}]}],
         "sources":[{"id":"ffffffff-0000-0000-0000-000000000001","name":"Scanner folder","scope":"mine"}],
         "can_manage_shared":false}
        """;

    private readonly RoutedHandler _handler = new();

    public ViewDocumentRulesUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton<IEngineApiClient>(new EngineApiClient(http, NullLogger<EngineApiClient>.Instance));
        _handler.Set("GET", "/view/document-rules", Rules);
        _handler.Set("GET", "/view/catalogue", Catalogue);
    }

    [Fact]
    public void TheListShowsEachRuleInRunningOrderAsOnePlainSentence()
    {
        var cut = Render<DocumentRulesSettings>();

        cut.WaitForAssertion(() => Assert.Contains("Water bills", cut.Markup, StringComparison.Ordinal));
        var rows = cut.FindAll("li.view-rules__row");
        Assert.Equal(2, rows.Count);
        Assert.Contains("When the words contain “water” and it is a PDF: add tag Bills, set correspondent Acme, file automatically",
            rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("When it came from Scanner folder: add tag (removed)", rows[1].TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("input[aria-label='Water bills enabled']").HasAttribute("checked"));
        Assert.False(cut.Find("input[aria-label='Scans enabled']").HasAttribute("checked"));
        // The first rule cannot move up and the last cannot move down.
        Assert.True(cut.Find("button[aria-label='Move Water bills up']").HasAttribute("disabled"));
        Assert.True(cut.Find("button[aria-label='Move Scans down']").HasAttribute("disabled"));
        Assert.NotEmpty(cut.FindAll("button:contains('Add rule')"));
        Assert.DoesNotContain("Edit on a computer or tablet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAPhoneTheRulesAreReadOnlyWithANoteToUseAComputer()
    {
        var cut = Render<DocumentRulesSettings>(parameters => parameters.Add(component => component.ReadOnly, true));

        cut.WaitForAssertion(() => Assert.Contains("Water bills", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Edit on a computer or tablet", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button[aria-label^='Edit ']"));
        Assert.Empty(cut.FindAll("button[aria-label^='Delete ']"));
        Assert.Empty(cut.FindAll("button[aria-label^='Move ']"));
        Assert.Empty(cut.FindAll("input[role=switch]"));
        Assert.Empty(cut.FindAll("button:contains('Add rule')"));
        Assert.Empty(cut.FindAll("button:contains('Apply rules')"));
    }

    [Fact]
    public void MovingARuleSendsTheWholeNewOrder()
    {
        var cut = Render<DocumentRulesSettings>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Move Water bills down']")));

        cut.Find("button[aria-label='Move Water bills down']").Click();

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastBody("PUT", "/view/document-rules/order")));
        using var body = JsonDocument.Parse(_handler.LastBody("PUT", "/view/document-rules/order")!);
        Assert.Equal([RuleTwo.ToString(), RuleOne.ToString()],
            body.RootElement.GetProperty("rule_ids").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void SwitchingARuleOffKeepsEverythingItDoesIncludingActionsOnNamesYouCannotSeeRightNow()
    {
        var cut = Render<DocumentRulesSettings>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[aria-label='Scans enabled']")));

        cut.Find("input[aria-label='Water bills enabled']").Change(false);

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastBody("PUT", "/view/document-rules/{id}")));
        using var water = JsonDocument.Parse(_handler.LastBody("PUT", "/view/document-rules/{id}")!);
        Assert.False(water.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, water.RootElement.GetProperty("conditions").GetArrayLength());
        Assert.Equal(2, water.RootElement.GetProperty("actions").GetArrayLength());
        Assert.True(water.RootElement.GetProperty("files_automatically").GetBoolean());

        cut.Find("input[aria-label='Scans enabled']").Change(true);
        cut.WaitForAssertion(() =>
        {
            using var scans = JsonDocument.Parse(_handler.LastBody("PUT", "/view/document-rules/{id}")!);
            Assert.Equal(1, scans.RootElement.GetProperty("actions").GetArrayLength());
        });
    }

    [Fact]
    public void ApplyingTheRulesSaysHowManyDocumentsWereQueued()
    {
        _handler.Set("POST", "/view/document-rules/apply", """{"queued":7}""");
        var cut = Render<DocumentRulesSettings>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button:contains('Apply rules')")));

        cut.Find("button:contains('Apply rules')").Click();

        cut.WaitForAssertion(() => Assert.NotNull(_handler.LastBody("POST", "/view/document-rules/apply")));
    }

    [Fact]
    public void TheEditorBuildsTheRuleFromWhatThePersonPicksAndSavesItInOneRequest()
    {
        _handler.Set("POST", "/view/document-rules", """
            {"id":"11111111-0000-0000-0000-000000000003","name":"New","enabled":true,"match_mode":"any","sort_order":2,"files_automatically":true,"scope":"mine","conditions":[],"actions":[]}
            """);
        var dialog = new DialogSpy();
        var cut = RenderEditor(dialog);

        cut.Find("input.app-text-field").Input("Phone bills");
        cut.Find("select[aria-label='Match mode']").Change("any");
        cut.Find("input[aria-label='Text to look for']").Change("telephone");
        cut.Find("button:contains('Add condition')").Click();
        var kinds = cut.FindAll("select[aria-label='Condition']");
        kinds[1].Change("source");
        cut.Find("select[aria-label='Source']").Change(SourceId.ToString("D"));
        cut.Find("button:contains('Add action')").Click();
        cut.Find("select[aria-label='Tag']").Change(BillsTag.ToString("D"));
        cut.Find("button:contains('Add action')").Click();
        cut.FindAll("select[aria-label='Action']")[1].Change("set_field");
        cut.Find("select[aria-label='Field']").Change(CostField.ToString("D"));
        cut.Find("input[aria-label='Value']").Change("12.50 eur");
        cut.Find("input[aria-label='File automatically']").Change(true);
        cut.Find("button:contains('Save rule')").Click();

        cut.WaitForAssertion(() => Assert.NotNull(dialog.Result));
        Assert.False(dialog.Result!.Canceled);
        using var body = JsonDocument.Parse(_handler.LastBody("POST", "/view/document-rules")!);
        var root = body.RootElement;
        Assert.Equal("Phone bills", root.GetProperty("name").GetString());
        Assert.Equal("any", root.GetProperty("match_mode").GetString());
        Assert.True(root.GetProperty("files_automatically").GetBoolean());
        Assert.Equal("mine", root.GetProperty("scope").GetString());
        var conditions = root.GetProperty("conditions").EnumerateArray().ToList();
        Assert.Equal(("text_contains", "telephone"), (conditions[0].GetProperty("kind").GetString(), conditions[0].GetProperty("value").GetString()));
        Assert.Equal(("source", SourceId.ToString("D")), (conditions[1].GetProperty("kind").GetString(), conditions[1].GetProperty("value").GetString()));
        var actions = root.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal(("add_tag", BillsTag.ToString("D")), (actions[0].GetProperty("kind").GetString(), actions[0].GetProperty("ref_id").GetString()));
        Assert.Equal("12.50 eur", actions[1].GetProperty("text_value").GetString());
    }

    [Fact]
    public void ARefusedRuleKeepsTheEditorOpenWithTheEnginesReason()
    {
        _handler.Set("POST", "/view/document-rules", """{"title":"Bad Request","detail":"A rule needs a name of up to 120 characters."}""", HttpStatusCode.BadRequest);
        var dialog = new DialogSpy();
        var cut = RenderEditor(dialog);

        cut.Find("button:contains('Save rule')").Click();

        cut.WaitForAssertion(() => Assert.Contains("needs a name", cut.Markup, StringComparison.Ordinal));
        Assert.Null(dialog.Result);
    }

    [Fact]
    public void TheTestButtonListsWhichOfMyDocumentsWouldMatch()
    {
        _handler.Set("POST", "/view/document-rules/test", """
            {"matches":[{"item_id":"21111111-0000-0000-0000-000000000001","title":"Water bill March"},{"item_id":"21111111-0000-0000-0000-000000000002","title":"Water bill April"}],
             "documents_checked":40,"has_more":false}
            """);
        var cut = RenderEditor(new DialogSpy());
        cut.Find("input[aria-label='Text to look for']").Change("water");

        cut.Find("button:contains('Test on my documents')").Click();

        cut.WaitForAssertion(() => Assert.Contains("Water bill March", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Water bill April", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("2 matching documents", cut.Markup, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(_handler.LastBody("POST", "/view/document-rules/test")!);
        Assert.Equal("water", body.RootElement.GetProperty("conditions")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void OnlyAHouseholdAdministratorIsOfferedTheSharedLibraryAndOnlyForANewRule()
    {
        var plain = RenderEditor(new DialogSpy());
        Assert.Empty(plain.FindAll("select[aria-label='Applies to']"));

        var admin = RenderEditor(new DialogSpy(), canManageShared: true);
        Assert.NotEmpty(admin.FindAll("select[aria-label='Applies to']"));

        var existing = RenderEditor(new DialogSpy(), canManageShared: true, rule: JsonSerializer.Deserialize<ViewDocumentRulesDto>(Rules)!.Rules[0]);
        Assert.Empty(existing.FindAll("select[aria-label='Applies to']"));
        Assert.Equal("Water bills", existing.Find("input.app-text-field").GetAttribute("value"));
    }

    private IRenderedComponent<DocumentRuleEditorDialog> RenderEditor(DialogSpy dialog, bool canManageShared = false, ViewDocumentRuleDto? rule = null) =>
        Render<DocumentRuleEditorDialog>(parameters => parameters
            .Add(component => component.Rule, rule)
            .Add(component => component.Sources, JsonSerializer.Deserialize<ViewDocumentRulesDto>(Rules)!.Sources)
            .Add(component => component.CanManageShared, canManageShared)
            .Add(component => component.Catalogue, JsonSerializer.Deserialize<ViewDocumentCatalogueDto>(Catalogue))
            .AddCascadingValue<IAppDialogContext>(dialog));

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
            var path = System.Text.RegularExpressions.Regex.Replace(
                request.RequestUri!.AbsolutePath, "/document-rules/[0-9a-fA-F-]{36}", "/document-rules/{id}");
            var key = $"{request.Method} {path}";
            if (request.Content is not null)
            {
                _bodies[key] = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var (json, status) = _routes.TryGetValue(key, out var found) ? found : ("{}", HttpStatusCode.OK);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
