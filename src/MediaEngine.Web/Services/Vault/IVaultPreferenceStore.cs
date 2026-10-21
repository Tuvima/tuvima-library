using System.Text.Json;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Vault;

/// <summary>Where a profile's Vault viewing options are kept between visits.</summary>
public interface IVaultPreferenceStore
{
    ValueTask<VaultViewPreferences> LoadAsync(Guid profileId);

    ValueTask SaveAsync(Guid profileId, VaultViewPreferences preferences);
}

/// <summary>
/// Keeps the options in this browser's local storage through <c>wwwroot/js/vault-preferences.js</c>. They are only read after
/// the Engine has said this profile can use NSFW mode, so no other profile ever touches the key. When storage is blocked the
/// defaults are used.
/// </summary>
public sealed class BrowserVaultPreferenceStore(IJSRuntime js, ILogger<BrowserVaultPreferenceStore> logger) : IVaultPreferenceStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async ValueTask<VaultViewPreferences> LoadAsync(Guid profileId)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/vault-preferences.js");
            var saved = await module.InvokeAsync<string?>("load", profileId.ToString("N"));
            return string.IsNullOrWhiteSpace(saved)
                ? VaultViewPreferences.Default
                : JsonSerializer.Deserialize<VaultViewPreferences>(saved, Json) ?? VaultViewPreferences.Default;
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException or JsonException)
        {
            logger.LogDebug(exception, "Could not read the Vault viewing options; using the defaults.");
            return VaultViewPreferences.Default;
        }
    }

    public async ValueTask SaveAsync(Guid profileId, VaultViewPreferences preferences)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/vault-preferences.js");
            await module.InvokeAsync<bool>("save", profileId.ToString("N"), JsonSerializer.Serialize(preferences, Json));
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException)
        {
            logger.LogDebug(exception, "Could not save the Vault viewing options.");
        }
    }
}
