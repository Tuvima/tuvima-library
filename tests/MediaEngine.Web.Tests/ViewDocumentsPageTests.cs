using System.Net;
using System.Text;
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

public sealed class ViewDocumentsPageTests : AsyncBunitContext
{
    private static readonly Guid LibraryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly DocumentsHandler _handler = new();

    public ViewDocumentsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Services.AddSingleton<IEngineApiClient>(api);
        // A desktop with a mouse: editing a document's details is desktop-only.
        var device = new DeviceContextService(api);
        device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)).GetAwaiter().GetResult();
        Services.AddSingleton(device);
        Services.AddScoped<ActiveProfileAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ShowsALoadingStateBeforeTheDocumentsArrive()
    {
        _handler.Hold();
        var cut = Render<ViewDocumentsPage>();

        Assert.Contains("Loading documents", cut.Markup, StringComparison.Ordinal);
        _handler.Release();
    }

    [Fact]
    public void ShowsWhereDocumentsComeFromWhenThereAreNone()
    {
        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains("No documents yet", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Documents added to View sources or the Import folder appear here.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Add media", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowsDocumentsWithKindChipsDatesAndAHighlightedSnippet()
    {
        _handler.Assets = "{\"items\":["
            + Item("lease.pdf", "application/pdf", "Lease agreement", snippet: "the \u0001zeppelin\u0002 hangar", textSource: "embedded")
            + ","
            + Item("scan.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", null, textSource: "ocr")
            + "],\"next_cursor\":\"abc\",\"has_more\":true}";
        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Lease agreement", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(2, cut.FindAll(".view-document-card").Count);
        Assert.Equal("zeppelin", cut.Find(".view-document-card__snippet mark").TextContent);
        Assert.Contains("PDF", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Word", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Scan", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Load more", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[style]"));
    }

    [Fact]
    public void TellsAProfileWithoutDocumentAccessThatDocumentsAreNotAvailable()
    {
        _handler.CanViewDocuments = false;
        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Documents are not available for this profile", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".view-document-card"));
        Assert.DoesNotContain(_handler.Requests, request => request.StartsWith("/view/assets", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchAsksTheEngineForDocumentsOnlyAndShowsAPlainNoMatchMessage()
    {
        var cut = Render<ViewDocumentsPage>();
        cut.WaitForAssertion(() => Assert.Contains("No documents yet", cut.Markup, StringComparison.Ordinal));

        cut.Find("input[type=search]").Input("zeppelin");

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Contains("q=zeppelin", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));
        var search = _handler.Requests.Last(request => request.StartsWith("/view/assets", StringComparison.Ordinal));
        Assert.Contains("kind=document", search, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("No documents match", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void TheViewShellAloneLoadsTheViewSettingsAndThePageWaitsForThem()
    {
        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains("No documents yet", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, _handler.Requests.Count(request => request.StartsWith("/view/scopes", StringComparison.Ordinal)));
        Assert.Equal(1, _handler.Requests.Count(request => request.StartsWith("/view/preferences", StringComparison.Ordinal) && !request.Contains("documents-sort", StringComparison.Ordinal)));
    }

    [Fact]
    public void ChoosingATagNarrowsTheListAndEachCardOffersEditingInYourOwnSpace()
    {
        _handler.Catalogue = "{\"tags\":[{\"id\":\"aaaaaaaa-0000-0000-0000-000000000001\",\"name\":\"Tax\",\"colour\":null,\"usage_count\":3,\"can_manage\":true}],"
            + "\"correspondents\":[{\"id\":\"bbbbbbbb-0000-0000-0000-000000000001\",\"name\":\"Acme\",\"colour\":null,\"usage_count\":2,\"can_manage\":true}],\"document_types\":[],\"fields\":[]}";
        _handler.Assets = "{\"items\":[" + Item("lease.pdf", "application/pdf", "Lease agreement") + "],\"next_cursor\":null,\"has_more\":false}";
        var cut = Render<ViewDocumentsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Tax (3)", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Acme (2)", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("button.view-document-card__edit"));
        Assert.Contains(_handler.Requests, request => request.StartsWith("/view/catalogue", StringComparison.Ordinal) && request.Contains("kind=document", StringComparison.Ordinal));

        cut.Find("input[type=checkbox]").Change(true);

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.StartsWith("/view/assets", StringComparison.Ordinal)
            && request.Contains("tag=aaaaaaaa-0000-0000-0000-000000000001", StringComparison.Ordinal)));
    }

    [Fact]
    public void NotesDescribeHowADocumentsWordsWereReadWithoutAnyErrors()
    {
        var scanned = Parse(Item("scan.pdf", "application/pdf", null, textSource: "ocr"));
        var locked = Parse(Item("locked.pdf", "application/pdf", null, textSource: "none", encrypted: true));
        var empty = Parse(Item("blank.pdf", "application/pdf", null, textSource: "none"));
        var longText = Parse(Item("long.txt", "text/plain", null, textSource: "embedded", truncated: true));
        var photo = Parse(Item("photo.jpg", "image/jpeg", null, textSource: "none"));

        Assert.Contains("Text read by OCR.", ViewDocumentPresentation.TextNotes(scanned));
        Assert.Contains("Password-protected, so its words are not searchable.", ViewDocumentPresentation.TextNotes(locked));
        Assert.Contains("No searchable words were found in this file.", ViewDocumentPresentation.TextNotes(empty));
        Assert.Contains(ViewDocumentPresentation.TextNotes(longText), note => note.Contains("only the first part", StringComparison.Ordinal));
        Assert.DoesNotContain(ViewDocumentPresentation.TextNotes(photo, isDocument: false), note => note.Contains("No searchable", StringComparison.Ordinal));
    }

    [Fact]
    public void SnippetPartsNeverTreatDocumentTextAsMarkup()
    {
        var parts = ViewDocumentPresentation.SnippetParts("<b>a</b> \u0001word\u0002 end");

        Assert.Equal([("<b>a</b> ", false), ("word", true), (" end", false)], parts);
    }

    [Fact]
    public void ShellOffersTheDocumentsTabWhenAllowed()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/view/documents");
        var allowed = Render<ViewSectionShell>(parameters => parameters.AddChildContent("<p>content</p>"));
        allowed.WaitForAssertion(() => Assert.Equal("Documents", allowed.Find("#media-section-nav-view-documents").TextContent.Trim()));
    }

    [Fact]
    public void ShellHidesTheDocumentsTabWhenNotAllowed()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/view");
        _handler.CanViewDocuments = false;
        var blocked = Render<ViewSectionShell>(parameters => parameters.AddChildContent("<p>content</p>"));
        blocked.WaitForAssertion(() => Assert.NotEmpty(blocked.FindAll("#media-section-nav-view")));
        Assert.Empty(blocked.FindAll("#media-section-nav-view-documents"));
    }

    private static LocalAssetDto Parse(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<LocalAssetDto>(json)!;

    private static string Item(string file, string mime, string? title, string? snippet = null, string? textSource = null, bool encrypted = false, bool truncated = false)
    {
        var info = textSource is null
            ? "null"
            : "{\"text_source\":\"" + textSource + "\",\"text_truncated\":" + truncated.ToString().ToLowerInvariant()
                + ",\"encrypted\":" + encrypted.ToString().ToLowerInvariant()
                + ",\"has_text\":" + (textSource == "none" ? "false" : "true") + "}";
        var titleJson = title is null ? "null" : "\"" + title + "\"";
        var snippetJson = snippet is null ? "null" : System.Text.Json.JsonSerializer.Serialize(snippet);
        return "{\"id\":\"" + Guid.NewGuid() + "\",\"library_id\":\"" + LibraryId + "\",\"media_kind\":\"document\",\"title\":" + titleJson
            + ",\"file_name\":\"" + file + "\",\"mime_type\":\"" + mime + "\",\"created_at\":\"2026-10-01T10:00:00+00:00\",\"captured_at\":null,"
            + "\"source_count\":1,\"files\":[],\"tags\":[\"tax\"],\"thumbnail_url\":\"/t\",\"content_url\":\"/c\",\"favorite\":false,\"hidden\":false,"
            + "\"snippet\":" + snippetJson + ",\"document_info\":" + info + "}";
    }

    private sealed class DocumentsHandler : HttpMessageHandler
    {
        private TaskCompletionSource<bool>? _gate;
        public List<string> Requests { get; } = [];
        public bool CanViewDocuments { get; set; } = true;
        public string Assets { get; set; } = "{\"items\":[],\"next_cursor\":null,\"has_more\":false}";
        public string Catalogue { get; set; } = "{\"tags\":[],\"correspondents\":[],\"document_types\":[],\"fields\":[]}";

        public void Hold() => _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate?.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            var json = request.RequestUri.AbsolutePath switch
            {
                "/profiles" => "[]",
                "/view/preferences" => "{\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"scope\":1,\"scope_profile_id\":null,\"timeline_density\":1,\"viewer_info_open\":true,\"updated_at\":null}",
                "/view/scopes" => "{\"scope\":{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"was_fallback\":false},\"available_scopes\":[{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"label\":\"Mine\",\"avatar_color\":null,\"avatar_url\":null}],\"can_browse_other_people\":false,\"can_view_documents\":"
                    + CanViewDocuments.ToString().ToLowerInvariant() + "}",
                "/view/assets" => Assets,
                "/view/catalogue" => Catalogue,
                _ => "{\"owned\":[],\"shared_with_you\":[]}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
