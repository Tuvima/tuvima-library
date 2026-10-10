using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class AccountSecurityRenderTests : AsyncBunitContext
{
    private readonly AccountSecurityClientFactory _factory = new();
    public AccountSecurityRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IHttpClientFactory>(_factory);
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddSingleton(System.Reflection.DispatchProxy.Create<IEngineApiClient, EmptyEngineApiClient>());
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Services.AddSingleton<IReadOnlyList<RegisteredExternalAuthProvider>>([]);
        Render<AppPopoverHost>();
    }

    [Fact]
    public void AccountLoadingRendersThreeSizedSkeletonsBeforeTheRequestCompletes()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        _factory.Pending = pending;
        var cut = Render<AccountSettingsTab>();
        Assert.Equal(new[] { "width:;height:112px", "width:;height:170px", "width:;height:140px" },
            cut.FindAll(".app-skeleton").Select(element => element.GetAttribute("style")));
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".app-skeleton")));
    }

    /// <summary>An Engine client that answers every call with nothing, for components that only need it to exist.</summary>
    public class EmptyEngineApiClient : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod?.ReturnType;
            if (type is null || type == typeof(void))
            {
                return null;
            }

            if (type == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GetGenericArguments()[0];
                var value = result.IsValueType ? Activator.CreateInstance(result) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class LoadingAccountClientFactory(TaskCompletionSource<HttpResponseMessage> pending) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new LoadingAccountHandler(pending))
        {
            BaseAddress = new Uri("https://engine.example.test"),
        };
    }

    private sealed class LoadingAccountHandler(TaskCompletionSource<HttpResponseMessage> pending) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            pending.Task.WaitAsync(cancellationToken);
    }

    [Fact]
    public void AccountSecurity_WithNoPasskeys_RendersPasskeyEmptyStateWithoutParameterErrors()
    {
        var cut = Render<AccountSettingsTab>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Passkeys", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("No passkeys added yet", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Passkeys are not available on this server.", cut.Markup, StringComparison.Ordinal);
        });
    }

    private sealed class AccountSecurityClientFactory : IHttpClientFactory
    {
        public TaskCompletionSource<HttpResponseMessage>? Pending { get; set; }
        public HttpClient CreateClient(string name) => Pending is not null
            ? new LoadingAccountClientFactory(Pending).CreateClient(name)
            : new(new AccountSecurityHandler())
            {
                BaseAddress = new Uri("https://engine.example.test"),
            };
    }

    private sealed class AccountSecurityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/access/self-service" => JsonResponse(new AccountSelfServiceResponse(
                    Guid.NewGuid(),
                    "owner@example.test",
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    [],
                    [],
                    new AccountSecurityCapabilitiesResponse(
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        false,
                        []))),
                "/auth/sessions" => JsonResponse(Array.Empty<DeviceSessionResponse>()),
                "/auth/passkeys" => JsonResponse(Array.Empty<PasskeyCredentialResponse>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };

            return Task.FromResult(response);
        }

        private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value),
        };
    }
}
