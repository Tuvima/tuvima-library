using System.Text;
using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Service;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugins;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The plugin door (<c>/plugin-host/v1</c>): the only way a plugin that is a separate program reaches the Library. It is
/// published as an OpenAPI description (<c>docs/reference/plugin-host-v1.openapi.json</c>) so a plugin can be written in any
/// language. Every call carries the plugin's own key in <c>X-Tuvima-Plugin-Key</c>; the key names one plugin and the Library
/// answers only for what that plugin was approved to do (the same permission checks the in-process plugins pass). The routes
/// are open to the framework's sign-in check on purpose: a program has no person to sign in, so the key is the sign-in. They
/// answer only callers on this machine.
/// </summary>
internal static class PluginHostEndpoints
{
    private const int MaxBodyBytes = 65_536;

    internal static RouteGroupBuilder MapPluginHostEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/plugin-host/v1")
            .WithTags("Plugin host")
            .AllowAnonymous();

        group.MapGet("/whoami", (HttpContext http, PluginKeyRegistry keys, PluginCatalog catalog) =>
            WithPlugin(http, keys, catalog, registration => Task.FromResult(Results.Ok(new PluginHostWhoAmIResponse
            {
                plugin_id = registration.Manifest.Id,
                name = registration.Manifest.Name,
                permissions = registration.Manifest.Permissions,
            }))))
            .WithName("PluginHostWhoAmI")
            .WithSummary("Who the key belongs to and what that plugin was approved to do.")
            .Produces<PluginHostWhoAmIResponse>(StatusCodes.Status200OK);

        group.MapPost("/ai/text", (
            HttpContext http,
            PluginKeyRegistry keys,
            PluginCatalog catalog,
            PluginAiGateway ai,
            CancellationToken ct) =>
            WithPlugin(http, keys, catalog, async registration =>
            {
                var body = await ReadJsonAsync(http, ct).ConfigureAwait(false);
                if (body is null
                    || !body.Value.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                    || !body.Value.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(prompt.GetString()))
                {
                    return ApiErrors.BadRequest("Send JSON with a \"role\" and a \"prompt\".");
                }

                var result = await ai.InferTextAsync(registration.Manifest.Id, role.GetString()!, prompt.GetString()!, options: null, ct).ConfigureAwait(false);
                return Results.Ok(new PluginHostAiTextResponse { status = PluginEndpoints.AiStatusWord(result.Status), message = result.Message, text = result.Text });
            }))
            .WithName("PluginHostAiText")
            .WithSummary("Ask Local AI for text as this plugin. Needs the ai.infer permission and a declared role; answers a plain status when AI cannot help.")
            .Produces<PluginHostAiTextResponse>(StatusCodes.Status200OK);

        group.MapGet("/records/{collection}/{key}", (
            string collection,
            string key,
            HttpContext http,
            PluginKeyRegistry keys,
            PluginCatalog catalog,
            IPluginExecutionContextFactory contexts,
            CancellationToken ct) =>
            WithPlugin(http, keys, catalog, async registration =>
            {
                await using var context = contexts.Create(registration.Manifest.Id, "service");
                PluginRecord? record;
                try
                {
                    record = await context.Records.GetAsync(collection, key, ct).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    return ApiErrors.BadRequest(ex.Message);
                }

                return record is null
                    ? ApiErrors.NotFound("There is no such record.")
                    : Results.Ok(new PluginHostRecordResponse
                    {
                        collection = record.Collection,
                        key = record.Key,
                        value = ParseRecord(record.Json),
                        updated_at = record.UpdatedAt,
                    });
            }))
            .WithName("PluginHostGetRecord")
            .WithSummary("Read one of this plugin's own records. Needs the plugin-records permission.")
            .Produces<PluginHostRecordResponse>(StatusCodes.Status200OK);

        group.MapPut("/records/{collection}/{key}", (
            string collection,
            string key,
            HttpContext http,
            PluginKeyRegistry keys,
            PluginCatalog catalog,
            IPluginExecutionContextFactory contexts,
            CancellationToken ct) =>
            WithPlugin(http, keys, catalog, async registration =>
            {
                var text = await ReadTextAsync(http, ct).ConfigureAwait(false);
                if (text is null)
                {
                    return ApiErrors.BadRequest($"The record must be JSON of at most {MaxBodyBytes / 1024} KB.");
                }

                await using var context = contexts.Create(registration.Manifest.Id, "service");
                try
                {
                    await context.Records.PutAsync(collection, key, text, ct).ConfigureAwait(false);
                }
                catch (PluginLimitExceededException ex)
                {
                    return ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, "Too much to keep", ex.Message);
                }
                catch (ArgumentException ex)
                {
                    return ApiErrors.BadRequest(ex.Message);
                }

                return Results.NoContent();
            }))
            .WithName("PluginHostPutRecord")
            .WithSummary("Save one of this plugin's own records. Needs the plugin-records permission; limited in size and count.")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }

    /// <summary>Checks the key, then runs the call as the plugin the key names. A wrong key, a switched-off plugin or a missing permission is a plain refusal.</summary>
    private static async Task<IResult> WithPlugin(
        HttpContext http,
        PluginKeyRegistry keys,
        PluginCatalog catalog,
        Func<PluginRegistration, Task<IResult>> call)
    {
        // Same machine only: the caller must be on a loopback address (or the Library's own address), the connection must not
        // have been passed on by a proxy or tunnel, and an unknown caller is refused rather than let through.
        var remote = http.Connection.RemoteIpAddress;
        var forwarded = http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("Forwarded")
            || http.Request.Headers.ContainsKey("X-Real-IP");
        if (remote is null || forwarded || !(System.Net.IPAddress.IsLoopback(remote) || remote.Equals(http.Connection.LocalIpAddress)))
        {
            return ApiErrors.Forbidden("The plugin door answers only programs on this machine.");
        }

        var pluginId = keys.Resolve(http.Request.Headers[PluginServiceClient.KeyHeader].ToString());
        var registration = pluginId is null ? null : catalog.Get(pluginId);
        if (registration is null || !registration.Enabled || registration.LoadError is not null)
        {
            return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "Plugin key refused", "The plugin key is missing, wrong or no longer valid.");
        }

        try
        {
            return await call(registration).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ApiErrors.Forbidden(ex.Message);
        }
    }

    private static JsonElement ParseRecord(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<string?> ReadTextAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                return null;
            }
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        try
        {
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonElement?> ReadJsonAsync(HttpContext http, CancellationToken ct) =>
        await ReadTextAsync(http, ct).ConfigureAwait(false) is { } text
            ? JsonDocument.Parse(text).RootElement.Clone()
            : null;
}
