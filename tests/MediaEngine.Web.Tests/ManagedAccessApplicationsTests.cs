using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ManagedAccessApplicationsTests : AsyncBunitContext
{
    private readonly ApplicationsHandler _handler = new();

    public ManagedAccessApplicationsTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => AdministratorSession());
        // bUnit's one-second default is shorter than a cold-start render on a busy CI runner.
        DefaultWaitTimeout = TimeSpan.FromSeconds(10);
    }

    [Fact]
    public void EditDisabledApplication_PreservesStatusAndSavesPermissionsAndBindings()
    {
        var cut = RenderApplications();
        OpenEdit(cut);

        PermissionCheckbox(cut, "Write playback progress").Change(true);
        ButtonNamed(cut, "Save application").Click();

        cut.WaitForAssertion(() => Assert.Contains("Living room was saved.", cut.Markup));
        var update = Assert.Single(_handler.Requests,
            request => request.Method == HttpMethod.Put && request.Path == $"/access/applications/{_handler.ApplicationId:D}");
        var updateBody = JsonSerializer.Deserialize<UpdateApplicationRequest>(update.Body, JsonOptions)!;
        Assert.False(updateBody.IsEnabled);
        Assert.Contains(_handler.Requests, request => request.Path.EndsWith("/permissions", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request => request.Path.EndsWith("/client-bindings", StringComparison.Ordinal));
    }

    [Fact]
    public void PresetAndManualPermissionSelection_ExposeCustomAndUnavailableStates()
    {
        var cut = RenderApplications();
        ButtonNamed(cut, "New application").Click();

        Assert.True(PermissionCheckbox(cut, "Unavailable camera access").HasAttribute("disabled"));
        cut.WaitForElement("select[aria-label='Permission preset']").Change("media-player");
        PermissionCheckbox(cut, "Write playback progress").Change(false);

        cut.WaitForAssertion(() => Assert.Equal("custom", cut.WaitForElement("select[aria-label='Permission preset']").GetAttribute("value")));
        Assert.Single(cut.FindAll("select[aria-label='Permission preset'] option"), option => option.TextContent == "Custom");
    }

    [Fact]
    public void TypeChange_RemovesIncompatiblePermissionsAndClearsBindingsBeforeUpdate()
    {
        var cut = RenderApplications();
        OpenEdit(cut);

        cut.WaitForElement("select[aria-label='Application type']").Change(nameof(ApplicationTypeDto.Automation));
        cut.WaitForAssertion(() => Assert.Contains("permission(s) incompatible with Automation were removed", cut.Markup));
        ButtonNamed(cut, "Save application").Click();

        cut.WaitForAssertion(() => Assert.Contains("Living room was saved.", cut.Markup));
        var requests = _handler.Requests.Where(request => request.Method == HttpMethod.Put).ToList();
        var clearIndex = requests.FindIndex(request => request.Path.EndsWith("/client-bindings", StringComparison.Ordinal));
        var updateIndex = requests.FindIndex(request => request.Path == $"/access/applications/{_handler.ApplicationId:D}");
        Assert.InRange(clearIndex, 0, updateIndex - 1);
    }

    [Fact]
    public void Credential_IsShownOnceAndClearedWhenDrawerCloses()
    {
        var cut = RenderApplications();
        OpenEdit(cut);
        ButtonNamed(cut, "Generate credential").Click();

        cut.WaitForAssertion(() => Assert.Contains(ApplicationsHandler.Secret, cut.Markup));
        cut.WaitForElement("button[aria-label='Close application drawer']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(ApplicationsHandler.Secret, cut.Markup);
            Assert.Empty(cut.FindAll("button[aria-label='Close application drawer']"));
        });
    }

    [Fact]
    public void IssuingAKey_ShowsTheApprovalDialog_AndIssuesOnlyAfterIssueKeyCarryingTheHash()
    {
        _handler.RequireApprovalHash = true;
        var dialogs = Render<AppDialogHost>();
        var cut = RenderApplications();
        OpenEdit(cut);

        ButtonNamed(cut, "Generate credential").Click();

        dialogs.WaitForAssertion(() =>
        {
            Assert.Contains("Issue this key?", dialogs.Markup);
            Assert.Contains("Reads your library", dialogs.Markup);
            Assert.Contains("Yes, to receiver.example", dialogs.Markup);
        });
        Assert.DoesNotContain(ApplicationsHandler.Secret, cut.Markup);
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Issue key").Click();

        cut.WaitForAssertion(() => Assert.Contains(ApplicationsHandler.Secret, cut.Markup));
        var posts = _handler.Requests.Where(request => request.Method == HttpMethod.Post && request.Path.EndsWith("/credentials", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Null(JsonSerializer.Deserialize<CreateApplicationCredentialRequest>(posts[0].Body, JsonOptions)!.ApprovalHash);
        Assert.Equal("hash-credential", JsonSerializer.Deserialize<CreateApplicationCredentialRequest>(posts[1].Body, JsonOptions)!.ApprovalHash);
    }

    [Fact]
    public void IssuingAKey_Cancel_IssuesNoKeyAndSendsNoSecondRequest()
    {
        _handler.RequireApprovalHash = true;
        var dialogs = Render<AppDialogHost>();
        var cut = RenderApplications();
        OpenEdit(cut);
        ButtonNamed(cut, "Generate credential").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Issue this key?", dialogs.Markup));

        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Issue this key?", dialogs.Markup));
        Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path.EndsWith("/credentials", StringComparison.Ordinal));
        Assert.DoesNotContain(ApplicationsHandler.Secret, cut.Markup);
    }

    [Fact]
    public void SavingAWebhook_ShowsTheApprovalDialog_AndSavesWithSaveCarryingTheHash()
    {
        _handler.ApplicationType = ApplicationTypeDto.Automation;
        _handler.RequireApprovalHash = true;
        var dialogs = Render<AppDialogHost>();
        var cut = RenderApplications();
        OpenEdit(cut);
        cut.WaitForAssertion(() => Assert.Contains("Add webhook", cut.Markup));
        ButtonNamed(cut, "Add webhook").Click();
        var editor = cut.WaitForElement(".application-webhook-editor");
        editor.QuerySelector("input[type='text']")!.Input("https://receiver.example/events");
        editor.QuerySelector(".application-webhook-events input[type='checkbox']")!.Change(true);
        ButtonNamed(cut, "Save webhook").Click();

        dialogs.WaitForAssertion(() => Assert.Contains("Save this webhook?", dialogs.Markup));
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Contains(ApplicationsHandler.WebhookSecret, cut.Markup));
        var posts = _handler.Requests.Where(request => request.Method == HttpMethod.Post
            && request.Path == $"/access/applications/{_handler.ApplicationId:D}/webhooks").ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("hash-webhook", JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(posts[1].Body, JsonOptions)!.ApprovalHash);
    }

    [Fact]
    public void SavingAWebhook_Cancel_SavesNothingAndKeepsTheFormOpen()
    {
        _handler.ApplicationType = ApplicationTypeDto.Automation;
        _handler.RequireApprovalHash = true;
        var dialogs = Render<AppDialogHost>();
        var cut = RenderApplications();
        OpenEdit(cut);
        cut.WaitForAssertion(() => Assert.Contains("Add webhook", cut.Markup));
        ButtonNamed(cut, "Add webhook").Click();
        var editor = cut.WaitForElement(".application-webhook-editor");
        editor.QuerySelector("input[type='text']")!.Input("https://receiver.example/events");
        editor.QuerySelector(".application-webhook-events input[type='checkbox']")!.Change(true);
        ButtonNamed(cut, "Save webhook").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Save this webhook?", dialogs.Markup));

        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Save this webhook?", dialogs.Markup));
        Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Post
            && request.Path == $"/access/applications/{_handler.ApplicationId:D}/webhooks");
        Assert.NotEmpty(cut.FindAll(".application-webhook-editor"));
        Assert.DoesNotContain(ApplicationsHandler.WebhookSecret, cut.Markup);
    }

    [Fact]
    public void CreateApplication_UsesSelectedPresetAndContinuesToCredentialSetup()
    {
        var cut = RenderApplications();
        ButtonNamed(cut, "New application").Click();
        ApplicationNameField(cut).Input("Kitchen display");
        ButtonNamed(cut, "Create application").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Kitchen display was created.", cut.Markup);
            Assert.Contains("Generate credential", cut.Markup);
        });
        var request = Assert.Single(_handler.Requests,
            request => request.Method == HttpMethod.Post && request.Path == "/access/applications");
        var payload = JsonSerializer.Deserialize<CreateApplicationRequest>(request.Body, JsonOptions)!;
        Assert.Equal(["library.read"], payload.PermissionIds);
    }

    [Fact]
    public async Task CreateBindingFailure_RetryUpdatesPersistedApplicationWithoutDuplicateCreate()
    {
        _handler.BindingStatus = HttpStatusCode.Conflict;
        var cut = RenderApplications();
        var newApplication = ButtonNamed(cut, "New application");
        await cut.InvokeAsync(() => newApplication.Click());
        var nameField = ApplicationNameField(cut);
        await cut.InvokeAsync(() => nameField.Input("Kitchen display"));
        var createApplication = ButtonNamed(cut, "Create application");
        await cut.InvokeAsync(() => createApplication.Click());

        cut.WaitForAssertion(() => Assert.Contains("native client bindings were not", cut.Markup));
        _handler.BindingStatus = HttpStatusCode.OK;
        var saveApplication = ButtonNamed(cut, "Save application");
        await cut.InvokeAsync(() => saveApplication.Click());

        cut.WaitForAssertion(() => Assert.Contains("Kitchen display was saved.", cut.Markup));
        Assert.Single(_handler.Requests,
            request => request.Method == HttpMethod.Post && request.Path == "/access/applications");
    }

    [Fact]
    public async Task DelayedCredential_DisablesSubmitAndCannotWriteIntoReopenedDrawer()
    {
        _handler.CredentialGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.CredentialStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderApplications();
        OpenEdit(cut);

        var pending = ButtonNamed(cut, "Generate credential")
            .ClickAsync(new());
        await _handler.CredentialStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Generate credential").HasAttribute("disabled")));

        cut.WaitForElement("button[aria-label='Close application drawer']").Click();
        ButtonNamed(cut, "New application").Click();
        _handler.CredentialGate.SetResult();
        await pending;

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("New application", cut.Markup);
            Assert.DoesNotContain(ApplicationsHandler.Secret, cut.Markup);
        });
        Assert.Single(_handler.Requests,
            request => request.Method == HttpMethod.Post && request.Path.EndsWith("/credentials", StringComparison.Ordinal));
    }

    [Fact]
    public void MutationConflict_KeepsDrawerOpenAndShowsUsefulError()
    {
        _handler.UpdateStatus = HttpStatusCode.Conflict;
        var cut = RenderApplications();
        OpenEdit(cut);
        ButtonNamed(cut, "Save application").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("This change conflicts with the current application state.", cut.Markup);
            Assert.Contains("Save application", cut.Markup);
        });
    }

    [Fact]
    public void ServerIntegration_ShowsWebhookDeliveryStateWithoutTestDeliveryAction()
    {
        _handler.ApplicationType = ApplicationTypeDto.ServerIntegration;
        var cut = RenderApplications();
        OpenEdit(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(ApplicationsHandler.WebhookUrl, cut.Markup);
            Assert.Contains("Retry scheduled after receiver failure", cut.Markup);
            Assert.Contains("Last attempt", cut.Markup);
            Assert.DoesNotContain("Test webhook", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Send test", cut.Markup, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void CreateWebhook_SendsBoundedConfigurationAndClearsSecretWhenDrawerCloses()
    {
        _handler.ApplicationType = ApplicationTypeDto.Automation;
        var cut = RenderApplications();
        OpenEdit(cut);
        cut.WaitForAssertion(() => Assert.Contains("Add webhook", cut.Markup));

        ButtonNamed(cut, "Add webhook").Click();
        var editor = cut.WaitForElement(".application-webhook-editor");
        editor.QuerySelector("input[type='text']")!.Input("https://receiver.example/events");
        editor.QuerySelector(".application-webhook-events input[type='checkbox']")!.Change(true);
        editor.QuerySelectorAll(".app-switch-row")
            .Single(row => row.TextContent.Contains("Allow private LAN receiver", StringComparison.Ordinal))
            .QuerySelector("input")!.Change(true);
        ButtonNamed(cut, "Save webhook").Click();

        cut.WaitForAssertion(() => Assert.Contains(ApplicationsHandler.WebhookSecret, cut.Markup));
        var create = Assert.Single(_handler.Requests, request =>
            request.Method == HttpMethod.Post
            && request.Path == $"/access/applications/{_handler.ApplicationId:D}/webhooks");
        var payload = JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(create.Body, JsonOptions)!;
        Assert.Equal("https://receiver.example/events", payload.Url);
        Assert.True(payload.AllowLocalNetwork);
        Assert.Equal([ApplicationsHandler.EventType], payload.EventTypes);

        cut.WaitForElement("button[aria-label='Close application drawer']").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain(ApplicationsHandler.WebhookSecret, cut.Markup));
    }

    [Fact]
    public void FailedWebhookMutation_ClearsPreviouslyRevealedSecret()
    {
        _handler.ApplicationType = ApplicationTypeDto.ServerIntegration;
        var cut = RenderApplications();
        OpenEdit(cut);
        cut.WaitForAssertion(() => Assert.Contains("Rotate signing secret", cut.Markup));

        ButtonNamed(cut, "Rotate signing secret").Click();
        cut.WaitForAssertion(() => Assert.Contains(ApplicationsHandler.WebhookSecret, cut.Markup));

        _handler.WebhookDeleteStatus = HttpStatusCode.Conflict;
        ButtonNamed(cut, "Delete").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(ApplicationsHandler.WebhookSecret, cut.Markup);
            Assert.Contains("This webhook changed", cut.Markup);
        });
    }

    [Fact]
    public void EditAndDeleteWebhook_UseVersionedCrudRoutes()
    {
        _handler.ApplicationType = ApplicationTypeDto.ServerIntegration;
        var cut = RenderApplications();
        OpenEdit(cut);
        cut.WaitForAssertion(() => Assert.Contains(ApplicationsHandler.WebhookUrl, cut.Markup));

        ButtonNamed(cut, "Edit").Click();
        cut.WaitForElement(".application-webhook-editor input[type='text']").Input("https://receiver.example/updated");
        ButtonNamed(cut, "Save webhook").Click();

        cut.WaitForAssertion(() => Assert.Contains("https://receiver.example/updated", cut.Markup));
        var update = Assert.Single(_handler.Requests, request =>
            request.Method == HttpMethod.Put
            && request.Path.EndsWith($"/webhooks/{_handler.WebhookId:D}", StringComparison.Ordinal));
        Assert.Equal(3, JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(update.Body, JsonOptions)!.ExpectedVersion);

        ButtonNamed(cut, "Delete").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("https://receiver.example/updated", cut.Markup));
        Assert.Contains(_handler.Requests, request =>
            request.Method == HttpMethod.Delete
            && request.Path.EndsWith($"/webhooks/{_handler.WebhookId:D}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClosingDrawer_CancelsPendingWebhookMutationWithoutRevealingSecret()
    {
        _handler.ApplicationType = ApplicationTypeDto.ServerIntegration;
        _handler.WebhookRotateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.WebhookRotateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.IgnoreWebhookRotateCancellation = true;
        var cut = RenderApplications();
        OpenEdit(cut);
        cut.WaitForAssertion(() => Assert.Contains("Rotate signing secret", cut.Markup));

        var pending = ButtonNamed(cut, "Rotate signing secret")
            .ClickAsync(new());
        await _handler.WebhookRotateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cut.WaitForElement("button[aria-label='Close application drawer']").Click();
        _handler.WebhookRotateGate.SetResult();
        await pending;

        Assert.DoesNotContain(ApplicationsHandler.WebhookSecret, cut.Markup);
        Assert.Empty(cut.FindAll("button[aria-label='Close application drawer']"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationChange_DiscardsDelayedRowsAndErrorsWithoutClearingCurrentLoading(bool oldLoadFails)
    {
        _handler.FirstWebhookLoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.FirstWebhookLoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SecondWebhookLoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SecondWebhookLoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.FirstWebhookLoadFails = oldLoadFails;
        var initial = _handler.ApplicationFor(_handler.ApplicationId, "Initial integration");
        var first = _handler.ApplicationFor(_handler.FirstRaceApplicationId, "First integration");
        var second = _handler.ApplicationFor(_handler.SecondRaceApplicationId, "Second integration");

        var cut = Render<ManagedApplicationWebhooks>(parameters => parameters.Add(component => component.Application, initial));
        cut.WaitForAssertion(() => Assert.Contains("Rotate signing secret", cut.Markup));
        ButtonNamed(cut, "Rotate signing secret").Click();
        cut.WaitForAssertion(() => Assert.Contains(ApplicationsHandler.WebhookSecret, cut.Markup));

        cut.Render(parameters => parameters.Add(component => component.Application, first));
        await _handler.FirstWebhookLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(ApplicationsHandler.WebhookSecret, cut.Markup);
        cut.Render(parameters => parameters.Add(component => component.Application, second));
        await _handler.SecondWebhookLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        _handler.FirstWebhookLoadGate.SetResult();
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[aria-label='Loading webhooks']"));
            Assert.DoesNotContain(ApplicationsHandler.FirstRaceWebhookUrl, cut.Markup);
            Assert.DoesNotContain("Webhooks could not be loaded", cut.Markup);
        });

        _handler.SecondWebhookLoadGate.SetResult();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(ApplicationsHandler.SecondRaceWebhookUrl, cut.Markup);
            Assert.DoesNotContain(ApplicationsHandler.FirstRaceWebhookUrl, cut.Markup);
            Assert.Empty(cut.FindAll("[aria-label='Loading webhooks']"));
        });
    }

    [Fact]
    public async Task DisposedComponent_IgnoresLateSuccessFromHandlerThatIgnoresCancellation()
    {
        _handler.WebhookRotateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.WebhookRotateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.IgnoreWebhookRotateCancellation = true;
        var application = _handler.ApplicationFor(_handler.ApplicationId, "Server integration");
        var cut = Render<ManagedApplicationWebhooks>(parameters => parameters.Add(component => component.Application, application));
        cut.WaitForAssertion(() => Assert.Contains("Rotate signing secret", cut.Markup));

        var pending = ButtonNamed(cut, "Rotate signing secret")
            .ClickAsync(new());
        await _handler.WebhookRotateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var instance = cut.Instance;
        await instance.DisposeAsync();
        cut.Dispose();
        _handler.WebhookRotateGate.SetResult();
        await pending;

        var secretField = typeof(ManagedApplicationWebhooks).GetField(
            "_signingSecret",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(secretField.GetValue(instance));
    }

    [Fact]
    public async Task OnAPhone_ApplicationsReadAsCardsWithoutManageControls()
    {
        await UsePhoneAsync();

        var cut = RenderApplications();

        Assert.NotEmpty(cut.FindAll(".app-table-shell--cards"));
        Assert.Equal("Living room", cut.WaitForElement("td[data-card-role='title']").TextContent.Trim());
        Assert.Equal("User Client", cut.WaitForElement("td[data-card-role='subtitle']").TextContent.Trim());
        Assert.Contains("Disabled", cut.WaitForElement("td[data-card-role='value']").TextContent);
        Assert.Contains("Manage on a computer or tablet", cut.WaitForElement("p[role='note']").TextContent);
        Assert.Empty(cut.FindAll("button[aria-label^='Actions for']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("New application"));
    }

    [Fact]
    public void OnADesktop_ApplicationsKeepTheRowActionsAndNoPhoneNote()
    {
        var cut = RenderApplications();

        Assert.Empty(cut.FindAll(".app-table-shell--cards"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Actions for Living room']"));
        Assert.DoesNotContain("Manage on a computer or tablet", cut.Markup);
    }

    private async Task UsePhoneAsync()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddScoped(_ => device);
    }

    private IRenderedComponent<ManagedAccessApplications> RenderApplications()
    {
        var cut = Render<ManagedAccessApplications>();
        cut.WaitForAssertion(() => Assert.Contains("Living room", cut.Markup));
        return cut;
    }

    private static void OpenEdit(IRenderedComponent<ManagedAccessApplications> cut)
    {
        cut.WaitForElement("button[aria-label='Actions for Living room']").Click();
        cut.WaitForAssertion(() => cut.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Edit application")
            .Click());
        cut.WaitForAssertion(() => Assert.Contains("Credentials", cut.Markup));
    }

    // Rows and buttons appear after a render, so lookups wait for the element instead of assuming the render already happened.
    private static AngleSharp.Dom.IElement PermissionCheckbox(
        IRenderedComponent<ManagedAccessApplications> cut,
        string label)
    {
        cut.WaitForState(() => cut.FindAll(".access-permission-row")
            .Any(row => row.TextContent.Contains(label, StringComparison.Ordinal)));
        return cut.FindAll(".access-permission-row")
            .Single(row => row.TextContent.Contains(label, StringComparison.Ordinal))
            .QuerySelector("input[type='checkbox']")!;
    }

    private static AngleSharp.Dom.IElement ButtonNamed<TComponent>(IRenderedComponent<TComponent> cut, string label)
        where TComponent : IComponent
    {
        cut.WaitForState(() => cut.FindAll("button").Any(button => button.TextContent.Trim() == label));
        return cut.FindAll("button").Single(button => button.TextContent.Trim() == label);
    }

    private static AngleSharp.Dom.IElement ApplicationNameField(IRenderedComponent<ManagedAccessApplications> cut)
    {
        cut.WaitForState(() => cut.FindAll(".access-form-section")
            .Any(section => section.TextContent.Contains("Application type", StringComparison.Ordinal)));
        return cut.FindAll(".access-form-section")
            .Single(section => section.TextContent.Contains("Application type", StringComparison.Ordinal))
            .QuerySelector("input[type='text']")!;
    }

    private static DashboardSessionAccessor AdministratorSession()
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, true, true, null, 1, [],
            ["settings.administration"], ["applications.manage"]));
        return session;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class ApplicationsHandler : HttpMessageHandler
    {
        public const string Secret = "one-time-application-secret";
        public const string WebhookSecret = "one-time-webhook-signing-secret";
        public const string WebhookUrl = "https://receiver.example/original";
        public const string EventType = "playback.session.changed.v1";
        public const string FirstRaceWebhookUrl = "https://receiver.example/first";
        public const string SecondRaceWebhookUrl = "https://receiver.example/second";
        public Guid ApplicationId { get; } = Guid.NewGuid();
        public Guid WebhookId { get; } = Guid.NewGuid();
        public Guid FirstRaceApplicationId { get; } = Guid.NewGuid();
        public Guid SecondRaceApplicationId { get; } = Guid.NewGuid();
        public HttpStatusCode UpdateStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode BindingStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode WebhookDeleteStatus { get; set; } = HttpStatusCode.NoContent;
        public TaskCompletionSource? CredentialGate { get; set; }
        public TaskCompletionSource? CredentialStarted { get; set; }
        public TaskCompletionSource? WebhookRotateGate { get; set; }
        public TaskCompletionSource? WebhookRotateStarted { get; set; }
        public TaskCompletionSource? FirstWebhookLoadGate { get; set; }
        public TaskCompletionSource? FirstWebhookLoadStarted { get; set; }
        public TaskCompletionSource? SecondWebhookLoadGate { get; set; }
        public TaskCompletionSource? SecondWebhookLoadStarted { get; set; }
        public bool FirstWebhookLoadFails { get; set; }
        public bool IgnoreWebhookRotateCancellation { get; set; }

        /// <summary>When set, issuing a key and saving a webhook answer 409 approval_required until the request carries the hash.</summary>
        public bool RequireApprovalHash { get; set; }
        public IReadOnlyList<RequestRecord> Requests
        {
            get
            {
                lock (_requestLock)
                {
                    return [.. _requests];
                }
            }
        }
        public ApplicationTypeDto ApplicationType { get => _type; set => _type = value; }

        private readonly object _requestLock = new();
        private readonly List<RequestRecord> _requests = [];
        private string _name = "Living room";
        private ApplicationTypeDto _type = ApplicationTypeDto.UserClient;
        private bool _enabled;
        private IReadOnlyList<string> _permissions = ["library.read", "progress.write"];
        private IReadOnlyList<string> _clients = ["tuvima-living-room"];
        private readonly List<ApplicationCredentialResponse> _credentials = [];
        private readonly List<ApplicationWebhookResponse> _webhooks = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requestLock)
            {
                _requests.Add(new(request.Method, path, body));
            }


            if (request.Method == HttpMethod.Get && path == "/access/applications")
            {
                return Json(new[] { Application() });
            }

            if (request.Method == HttpMethod.Get && path == "/access/applications/permissions")
            {
                return Json(Permissions());
            }

            if (request.Method == HttpMethod.Get && path == "/access/applications/presets")
            {
                return Json(Presets());
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/webhooks/event-types", StringComparison.Ordinal))
            {
                return Json(new[] { EventType, "library.item.changed.v1" });
            }

            if (request.Method == HttpMethod.Get && path == $"/access/applications/{FirstRaceApplicationId:D}/webhooks")
            {
                FirstWebhookLoadStarted?.TrySetResult();
                if (FirstWebhookLoadGate is not null)
                {
                    await FirstWebhookLoadGate.Task;
                }

                if (FirstWebhookLoadFails)
                {
                    throw new InvalidOperationException("Delayed first application failure");
                }

                return Json(new[] { RaceWebhook(FirstRaceApplicationId, FirstRaceWebhookUrl) });
            }
            if (request.Method == HttpMethod.Get && path == $"/access/applications/{SecondRaceApplicationId:D}/webhooks")
            {
                SecondWebhookLoadStarted?.TrySetResult();
                if (SecondWebhookLoadGate is not null)
                {
                    await SecondWebhookLoadGate.Task;
                }

                return Json(new[] { RaceWebhook(SecondRaceApplicationId, SecondRaceWebhookUrl) });
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/webhooks", StringComparison.Ordinal))
            {
                EnsureWebhook();
                return Json(_webhooks);
            }
            if (RequireApprovalHash
                && (request.Method == HttpMethod.Post && path.EndsWith("/webhooks", StringComparison.Ordinal)
                    || request.Method == HttpMethod.Put && path.EndsWith($"/webhooks/{WebhookId:D}", StringComparison.Ordinal))
                && JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(body, JsonOptions)!.ApprovalHash is null)
            {
                return ApprovalRequired("webhook", "Webhook to receiver.example", "hash-webhook");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/webhooks", StringComparison.Ordinal))
            {
                var value = JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(body, JsonOptions)!;
                var webhook = new ApplicationWebhookResponse(
                    WebhookId, ApplicationId, value.Url, value.EventTypes, value.AllowLocalNetwork,
                    value.IsEnabled, 1, "Waiting for new events", null, null);
                _webhooks.Clear();
                _webhooks.Add(webhook);
                return Json(new ApplicationWebhookSecretResponse(webhook, WebhookSecret));
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/rotate", StringComparison.Ordinal))
            {
                WebhookRotateStarted?.TrySetResult();
                if (WebhookRotateGate is not null)
                {
                    if (IgnoreWebhookRotateCancellation)
                    {
                        await WebhookRotateGate.Task;
                    }
                    else
                    {
                        await WebhookRotateGate.Task.WaitAsync(cancellationToken);
                    }
                }
                EnsureWebhook();
                var webhook = _webhooks[0] with { Version = _webhooks[0].Version + 1, LastStatus = "Signing secret rotated" };
                _webhooks[0] = webhook;
                return Json(new ApplicationWebhookSecretResponse(webhook, WebhookSecret));
            }
            if (request.Method == HttpMethod.Put && path.EndsWith($"/webhooks/{WebhookId:D}", StringComparison.Ordinal))
            {
                var value = JsonSerializer.Deserialize<SaveApplicationWebhookRequest>(body, JsonOptions)!;
                var webhook = new ApplicationWebhookResponse(
                    WebhookId, ApplicationId, value.Url, value.EventTypes, value.AllowLocalNetwork,
                    value.IsEnabled, value.ExpectedVersion.GetValueOrDefault() + 1, "Waiting for new events", null, null);
                _webhooks.Clear();
                _webhooks.Add(webhook);
                return Json(new ApplicationWebhookSecretResponse(webhook, null));
            }
            if (request.Method == HttpMethod.Delete && path.EndsWith($"/webhooks/{WebhookId:D}", StringComparison.Ordinal))
            {
                if (WebhookDeleteStatus != HttpStatusCode.NoContent)
                {
                    return new HttpResponseMessage(WebhookDeleteStatus);
                }

                _webhooks.Clear();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post && path == "/access/applications")
            {
                var value = JsonSerializer.Deserialize<CreateApplicationRequest>(body, JsonOptions)!;
                _name = value.Name;
                _type = value.ApplicationType;
                _enabled = true;
                _permissions = value.PermissionIds;
                return Json(Application(), HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/permissions", StringComparison.Ordinal))
            {
                _permissions = JsonSerializer.Deserialize<SetApplicationPermissionsRequest>(body, JsonOptions)!.PermissionIds;
                return Json(Application());
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/client-bindings", StringComparison.Ordinal))
            {
                if (BindingStatus != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(BindingStatus);
                }

                _clients = JsonSerializer.Deserialize<SetApplicationClientBindingsRequest>(body, JsonOptions)!.ClientIds;
                return Json(Application());
            }
            if (request.Method == HttpMethod.Put && path == $"/access/applications/{ApplicationId:D}")
            {
                if (UpdateStatus != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(UpdateStatus);
                }

                var value = JsonSerializer.Deserialize<UpdateApplicationRequest>(body, JsonOptions)!;
                _name = value.Name;
                _type = value.ApplicationType;
                _enabled = value.IsEnabled;
                return Json(Application());
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/credentials", StringComparison.Ordinal))
            {
                if (RequireApprovalHash && JsonSerializer.Deserialize<CreateApplicationCredentialRequest>(body, JsonOptions)!.ApprovalHash is null)
                {
                    return ApprovalRequired("application_credential", "Key for Living room", "hash-credential");
                }

                CredentialStarted?.TrySetResult();
                if (CredentialGate is not null)
                {
                    await CredentialGate.Task.WaitAsync(cancellationToken);
                }

                var credential = new ApplicationCredentialResponse(Guid.NewGuid(), ApplicationId, "Credential", DateTimeOffset.UtcNow, null, null, null);
                _credentials.Add(credential);
                return Json(new ApplicationCredentialIssuedResponse(credential, Secret), HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage ApprovalRequired(string kind, string title, string hash) => Json(
            new MediaEngine.Contracts.Approvals.ApprovalRequiredResponse
            {
                Approvals =
                [
                    new MediaEngine.Contracts.Approvals.ApprovalPreviewResponse
                    {
                        Kind = kind,
                        Title = title,
                        CanDo = ["Reads your library"],
                        SendsDataOut = true,
                        SendsDataTo = "receiver.example",
                        StatementHash = hash,
                    },
                ],
            },
            HttpStatusCode.Conflict);

        private void EnsureWebhook()
        {
            if (_webhooks.Count > 0)
            {
                return;
            }

            _webhooks.Add(new ApplicationWebhookResponse(
                WebhookId,
                ApplicationId,
                WebhookUrl,
                [EventType],
                false,
                true,
                3,
                "Retry scheduled after receiver failure",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow.AddHours(-1)));
        }

        private ApplicationWebhookResponse RaceWebhook(Guid applicationId, string url) => new(
            Guid.NewGuid(), applicationId, url, [EventType], false, true, 1,
            "Waiting for new events", null, null);

        public ApplicationResponse ApplicationFor(Guid id, string name) => new(
            id, name, null, ApplicationTypeDto.ServerIntegration, true, false, 1,
            ["events.subscribe"], [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        private ApplicationResponse Application() => new(
            ApplicationId, _name, "Television client", _type, _enabled, false, 1,
            _permissions, _clients, _credentials, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow, null);

        private static ApplicationPermissionDefinitionDto[] Permissions() =>
        [
            new("library.read", "Library", "Read library", "Browse catalogued media.", "Low", [ApplicationTypeDto.UserClient, ApplicationTypeDto.Automation], false, "BuiltIn", null, 1, true, null),
            new("progress.write", "Playback", "Write playback progress", "Update playback state.", "Sensitive", [ApplicationTypeDto.UserClient], true, "BuiltIn", null, 2, true, null),
            new("camera.read", "Device", "Unavailable camera access", "Read a device camera.", "High", [ApplicationTypeDto.UserClient], true, "Plugin", "camera", 3, false, "Camera plugin is not installed."),
        ];

        private static ApplicationPermissionPresetDto[] Presets() =>
        [
            new("read-only", "Read only", ["library.read"], false),
            new("media-player", "Media player", ["library.read", "progress.write"], false),
            new("administrator", "Administrator", [], true),
        ];

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);
}
