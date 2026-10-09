using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Security;

/// <summary>
/// Binds passkeys to the one public address (<c>network.remote.public_hostname</c>) instead of the
/// request's Host header: the relying-party domain is that address's host and the only accepted
/// origin is that address. With no valid public address no origin is accepted.
/// </summary>
public sealed class PublicAddressPasskeyOptions(IConfigurationLoader configuration) : IConfigureOptions<IdentityPasskeyOptions>
{
    public void Configure(IdentityPasskeyOptions options)
    {
        var network = configuration.LoadNetwork();
        if (!network.HasValidPublicAddress()
            || !Uri.TryCreate(network.Remote.PublicHostname!.Trim(), UriKind.Absolute, out var address))
        {
            options.ValidateOrigin = _ => ValueTask.FromResult(false);
            return;
        }

        var origin = address.GetLeftPart(UriPartial.Authority);
        options.ServerDomain = address.Host;
        options.ValidateOrigin = context => ValueTask.FromResult(
            !context.CrossOrigin
            && string.Equals(context.Origin?.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }
}
