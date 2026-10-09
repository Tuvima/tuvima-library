using System.Net;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Services.Configuration;

/// <summary>
/// The Host names the Dashboard answers to. Anything else is refused so a malicious website cannot reach
/// the Dashboard through DNS rebinding (a hostile name that resolves to this computer).
/// </summary>
public sealed class HostAllowList
{
    public const string RefusalMessage = "This address isn't allowed for Tuvima Library. Add it under Settings > Network.";

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase) { "localhost" };

    public HostAllowList(NetworkSettings network, string? tailscaleUrl, string machineName)
    {
        AddWithLocalSuffix(machineName);
        AddWithLocalSuffix(network.Local.PreferredServerName);
        AddUrlHost(network.Remote.PublicHostname);
        AddUrlHost(tailscaleUrl);
        foreach (var name in network.Local.AllowedHostnames ?? [])
        {
            Add(name);
        }
    }

    /// <summary>Names (not IP literals) that are allowed; exposed for forwarded-host validation and tests.</summary>
    public IReadOnlyCollection<string> Names => _names;

    public bool IsAllowed(HostString host)
    {
        var name = host.Host;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.StartsWith('[') && name.EndsWith(']'))
        {
            name = name[1..^1];
        }

        if (IPAddress.TryParse(name, out _))
        {
            return true;
        }

        return _names.Contains(name.TrimEnd('.'));
    }

    private void AddWithLocalSuffix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Add(name);
        Add($"{name.Trim()}.local");
    }

    private void AddUrlHost(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            Add(uri.Host);
        }
    }

    private void Add(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _names.Add(name.Trim().TrimEnd('.'));
        }
    }
}

public static class HostAllowListMiddlewareExtensions
{
    /// <summary>Refuses requests whose Host header is not on the allow-list. <c>/health/live</c> is exempt.</summary>
    public static IApplicationBuilder UseHostAllowList(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var allowList = context.RequestServices.GetRequiredService<HostAllowList>();
            if (allowList.IsAllowed(context.Request.Host)
                || context.Request.Path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(HostAllowList.RefusalMessage).ConfigureAwait(false);
        });
}
