using System.IO;
using System.Text.Json;
using MediaEngine.Contracts.Playback;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Reads the existing playback snapshot JSON through Blazor's bounded streaming interop.</summary>
public static class PlaybackSnapshotStream
{
    public const long MaximumBytes = 8L * 1024 * 1024;

    public static async Task<ListenPlaybackSnapshot?> ReadAsync(
        IJSStreamReference? reference,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (reference is null) return null;

        try
        {
            await using var stream = await reference.OpenReadStreamAsync(MaximumBytes, ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<ListenPlaybackSnapshot>(stream, options, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The playback snapshot stream contained JSON null.");
        }
        finally
        {
            try
            {
                await reference.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The stream may already have been torn down with its Blazor circuit.
            }
        }
    }
}
