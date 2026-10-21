using System.Reflection;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>Only a clean 11-character ID may start an online lookup; anything else is refused before any setting is read or any request is made.</summary>
public sealed class YoutubeVideoLookupIdTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ\n")]
    [InlineData("dQw4w9WgXcQ\r\n")]
    [InlineData("short")]
    [InlineData("")]
    public async Task LookupAsync_RefusesAnIdThatIsNotExactlyElevenIdCharacters_WithoutTouchingAnything(string id)
    {
        var lookup = new YoutubeVideoLookup(
            Untouchable<IConfigurationLoader>.Create(),
            Untouchable<IConfigSecretsStore>.Create(),
            new NoHttpFactory(),
            NullLogger<YoutubeVideoLookup>.Instance);

        Assert.Null(await lookup.LookupAsync(id));
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No request may be made.");
    }

    /// <summary>Any call fails the test, so an ID that gets past the guard cannot go unnoticed.</summary>
    private class Untouchable<T> : DispatchProxy
        where T : class
    {
        public static T Create() => Create<T, Untouchable<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod?.Name} must not be called.");
    }
}
