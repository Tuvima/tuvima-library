using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Components.Shared;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class AdministratorSurfaceGateTests : AsyncBunitContext
{
    private readonly DashboardSessionAccessor session = new();
    private readonly GateHandler handler = new();

    public AdministratorSurfaceGateTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(session);
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(handler));
        Services.AddScoped<DashboardIdentityClient>();
        SetAuthority(unlocked: false);
    }

    [Fact]
    public void LockedSurface_DoesNotRenderProtectedContent_ButPersonalSettingsStayAvailable()
    {
        var cut = RenderGate();
        Assert.DoesNotContain("protected-settings", cut.Markup);
        Assert.Contains("Administrator PIN", cut.Markup);
        cut.Render(parameters => parameters.Add(component => component.Required, false));
        Assert.Contains("protected-settings", cut.Markup);
    }

    [Fact]
    public async Task Revocation_RemovesAnAlreadyRenderedForm()
    {
        SetAuthority(unlocked: true);
        var cut = RenderGate();
        Assert.Contains("protected-settings", cut.Markup);
        await cut.InvokeAsync(() => SetAuthority(unlocked: false));
        cut.WaitForAssertion(() => Assert.DoesNotContain("protected-settings", cut.Markup));
    }

    [Fact]
    public async Task Unlock_RequiresLiveValidation_AndClearsPin()
    {
        var cut = RenderGate();
        handler.Unlocked = true;
        await cut.InvokeAsync(() => cut.Find("input[aria-label='Administrator PIN']").Input("1234"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Contains("Unlock settings")).Click());
        cut.WaitForAssertion(() => Assert.Contains("protected-settings", cut.Markup));
        Assert.Equal(1, handler.Unlocks);
        Assert.Equal(1, handler.Validations);
        Assert.DoesNotContain("1234", cut.Markup);
    }

    [Fact]
    public async Task FailedUnlock_DoesNotExposeContent_AndRemainsRetryable()
    {
        handler.FailUnlock = true;
        var cut = RenderGate();
        await cut.InvokeAsync(() => cut.Find("input[aria-label='Administrator PIN']").Input("1234"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Contains("Unlock settings")).Click());
        cut.WaitForAssertion(() => Assert.Contains("Engine is unavailable", cut.Markup));
        Assert.DoesNotContain("protected-settings", cut.Markup);
        Assert.DoesNotContain("1234", cut.Markup);
        handler.FailUnlock = false;
        handler.Unlocked = true;
        await cut.InvokeAsync(() => cut.Find("input[aria-label='Administrator PIN']").Input("5678"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Contains("Unlock settings")).Click());
        cut.WaitForAssertion(() => Assert.Contains("protected-settings", cut.Markup));
    }

    [Fact]
    public async Task Lock_RemovesFormBeforeServerReply()
    {
        SetAuthority(unlocked: true);
        handler.LockReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderGate();
        var locking = cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Contains("Lock settings")).ClickAsync(new()));
        await handler.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.DoesNotContain("protected-settings", cut.Markup));
        Assert.False(session.HasAction("access.manage"));
        handler.LockReply.SetResult();
        await locking;
    }

    [Fact]
    public async Task EditorEntry_PromptsForPin_ThenRevalidatesBeforeContinuing()
    {
        var cut = Render<AppDialogHost>();
        var access = new AdministratorSurfaceAccessService(Services.GetRequiredService<DashboardIdentityClient>(),
            session, Services.GetRequiredService<IAppDialogService>());
        Task<bool> pending = null!;
        await cut.InvokeAsync(() => { pending = access.EnsureUnlockedAsync(); });
        cut.WaitForElement("input[aria-label='Administrator PIN']");
        Assert.DoesNotContain("Continue to editor", cut.Markup);
        Assert.False(await access.EnsureUnlockedAsync());
        handler.Unlocked = true;
        await cut.InvokeAsync(() => cut.Find("input[aria-label='Administrator PIN']").Input("1234"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Contains("Unlock settings")).Click());
        cut.WaitForAssertion(() => Assert.Contains("Continue to editor", cut.Markup));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Contains("Continue to editor")).Click());
        Assert.True(await pending);
        Assert.Equal(3, handler.Validations);
    }

    [Fact]
    public async Task EditorEntry_CancelLeavesEditorClosed_AndCanRetry()
    {
        var cut = Render<AppDialogHost>();
        var access = new AdministratorSurfaceAccessService(Services.GetRequiredService<DashboardIdentityClient>(),
            session, Services.GetRequiredService<IAppDialogService>());
        Task<bool> pending = null!;
        await cut.InvokeAsync(() => { pending = access.EnsureUnlockedAsync(); });
        cut.WaitForElement("input[aria-label='Administrator PIN']");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click());
        Assert.False(await pending);
        handler.Unlocked = true;
        Assert.True(await access.EnsureUnlockedAsync());
    }

    private IRenderedComponent<AdministratorSurfaceGate> RenderGate() => Render<AdministratorSurfaceGate>(parameters =>
        parameters.Add(component => component.Required, true).AddChildContent("<div>protected-settings</div>"));

    private void SetAuthority(bool unlocked) => session.Set("session", handler.Account, handler.Profile, handler.Session, handler.Authority(unlocked));

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class GateHandler : HttpMessageHandler
    {
        public Guid Account { get; } = Guid.NewGuid();
        public Guid Profile { get; } = Guid.NewGuid();
        public Guid Session { get; } = Guid.NewGuid();
        public bool Unlocked { get; set; }
        public bool FailUnlock { get; set; }
        public int Unlocks { get; private set; }
        public int Validations { get; private set; }
        public TaskCompletionSource LockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? LockReply { get; set; }
        public DashboardAuthorityResponse Authority(bool unlocked) => new(
            Account, Profile, true, true, 1, 1, true, unlocked, null, 1, [],
            ["settings.administration"], unlocked ? ["access.manage", "administrator.lock"] : ["administrator.unlock"]);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/access/admin-unlock")
            {
                Unlocks++;
                if (FailUnlock)
                {
                    throw new HttpRequestException("Engine unavailable");
                }

                return Json(new GrantAdminUnlockResponse(Unlocked, null, 1));
            }
            if (request.Method == HttpMethod.Delete)
            {
                Unlocked = false;
                LockStarted.TrySetResult();
                if (LockReply is not null)
                {
                    await LockReply.Task.WaitAsync(ct);
                }

                return new(HttpStatusCode.NoContent);
            }
            Validations++;
            return Json(new SessionValidationResponse
            {
                AccountId = Account,
                ActiveProfileId = Profile,
                SessionId = Session,
                DisplayName = "Profile",
                Authority = Authority(Unlocked),
                AuthenticationMethod = "test",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
