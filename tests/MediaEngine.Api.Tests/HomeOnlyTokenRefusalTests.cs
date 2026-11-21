using System.Net;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

/// <summary>The Engine re-checks home-only app tokens itself instead of trusting the Dashboard's front door alone (issue 1236).</summary>
public sealed class HomeOnlyTokenRefusalTests
{
    private const string HomeOnlyToken = ClientIngress.LanOnlyTokenPrefix + "abc";
    private const string OrdinaryToken = "abc";

    private static DefaultHttpContext Context(string? place, string? remoteAddress = "192.168.1.20")
    {
        var context = new DefaultHttpContext();
        if (place is not null)
        {
            context.Request.Headers[ClientIngressValues.ValidateHeader] = place;
        }

        context.Connection.RemoteIpAddress = remoteAddress is null ? null : IPAddress.Parse(remoteAddress);
        return context;
    }

    [Theory]
    [InlineData(ClientIngress.HomeNetwork)]
    [InlineData(ClientIngress.ThisComputer)]
    public void HomeOnlyToken_IsAcceptedFromThisComputerOrTheHomeNetwork(string place) =>
        Assert.False(RequestIngress.HomeOnlyTokenRefused(Context(place), HomeOnlyToken));

    [Theory]
    [InlineData(ClientIngress.Remote)]
    [InlineData(ClientIngress.Outside)]
    [InlineData("something-else")]
    [InlineData(null)]
    public void HomeOnlyToken_IsRefusedFromAnyOtherOrMissingPlace(string? place) =>
        Assert.True(RequestIngress.HomeOnlyTokenRefused(Context(place), HomeOnlyToken));

    [Fact]
    public void HomeOnlyToken_IsRefusedWhenTheConnectionItselfIsPublicEvenIfTheHeaderSaysHome() =>
        Assert.True(RequestIngress.HomeOnlyTokenRefused(Context(ClientIngress.HomeNetwork, "8.8.8.8"), HomeOnlyToken));

    [Fact]
    public void HomeOnlyToken_IsRefusedWhenTheRequestWasForwarded()
    {
        var context = Context(ClientIngress.HomeNetwork);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        Assert.True(RequestIngress.HomeOnlyTokenRefused(context, HomeOnlyToken));
    }

    [Theory]
    [InlineData(ClientIngress.Outside)]
    [InlineData(null)]
    public void OrdinaryToken_IsNeverRefusedByThisRule(string? place) =>
        Assert.False(RequestIngress.HomeOnlyTokenRefused(Context(place), OrdinaryToken));
}
