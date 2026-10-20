using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Providers;

public sealed partial class TextTrackProviderAdapter
{
    private async Task<TextTrackDownload?> DownloadSubtitleAsync(TextTrackCandidate candidate, CancellationToken ct)
    {
        if (candidate.Payload is not DownloadSelection selection)
        {
            return null;
        }

        var availability = GetAvailability(_primaryMediaType);
        if (!availability.IsAvailable)
        {
            throw new TextTrackLookupException(
                availability.Status, availability.Message ?? Message(TextTrackMessages.Unreachable, "{display_name} is unavailable."));
        }

        var download = _text.Download;
        try
        {
            // An unpacked file carries its own link. For a simple release the provider is asked for
            // the link, and only answers when there is one unambiguous file.
            var url = selection.FileUrl ?? await ResolveLinkAsync(selection, ct).ConfigureAwait(false);
            if (!TryDownloadUri(url, out var downloadUri))
            {
                return null;
            }

            using var client = _httpFactory.CreateClient(Name);
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            using var responseFile = await _rateLimiter.ExecuteAsync(Name, _config.RateLimit,
                token => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token), ct)
                .ConfigureAwait(false);
            if (responseFile.RequestMessage?.RequestUri is { } finalFileUri
                && !TryDownloadUri(finalFileUri.ToString(), out _))
            {
                return null;
            }

            if (!responseFile.IsSuccessStatusCode || responseFile.Content.Headers.ContentLength > download.MaxBytes)
            {
                return null;
            }

            await using var stream = await responseFile.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > download.MaxBytes)
                {
                    return null;
                }

                buffer.Write(bytes, 0, read);
            }

            var payload = buffer.ToArray();
            if (payload.Length == 0)
            {
                return null;
            }

            var fileName = selection.FileName;
            if (IsZip(payload))
            {
                var extracted = ExtractSingleSafeEntry(payload, selection);
                if (extracted is null)
                {
                    return null;
                }

                (payload, fileName) = extracted.Value;
            }

            var format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (!download.Formats.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            var content = DecodeText(payload);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var normalized = SubtitleNormalizer.NormalizeToWebVtt(content, format);
            if (!normalized.Contains(" --> ", StringComparison.Ordinal))
            {
                return null;
            }

            await _health.ReportSuccessAsync(Name, ct).ConfigureAwait(false);
            return new TextTrackDownload(candidate, content, format, download.OutputFormat ?? format);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TextTrackLookupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(ex, "{Provider} subtitle download failed", Name);
            await _health.ReportFailureAsync(Name, "Subtitle download failed", ct).ConfigureAwait(false);
            throw new TextTrackLookupException(
                "ProviderUnavailable", Message(TextTrackMessages.DownloadFailed, "{display_name} could not complete the download."));
        }
    }

    private async Task<string?> ResolveLinkAsync(DownloadSelection selection, CancellationToken ct)
    {
        if (_text.Download.Resolve is not { } resolve)
        {
            return null;
        }

        using var response = await SendApiAsync(
            resolve.Path.Replace("{id}", Uri.EscapeDataString(selection.SubtitleId), StringComparison.Ordinal), ct)
            .ConfigureAwait(false);
        if (response is null)
        {
            throw new TextTrackLookupException("ProviderUnavailable", Message(TextTrackMessages.Unreachable, "{display_name} could not be reached."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, ct).ConfigureAwait(false);
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return FirstString(doc.RootElement, resolve.UrlPaths);
    }

    /// <summary>
    /// A download link is accepted only on a listed host over HTTPS with the default port, under the
    /// configured path, with no credentials, query, fragment or parent-directory segment.
    /// </summary>
    private bool TryDownloadUri(string? value, out Uri uri)
    {
        uri = null!;
        var download = _text.Download;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (download.PathPrefix is not null && download.RelativeBase is not null
            && value.StartsWith(download.PathPrefix, StringComparison.Ordinal))
        {
            value = download.RelativeBase + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps
            && download.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            && (download.PathPrefix is null || uri.AbsolutePath.StartsWith(download.PathPrefix, StringComparison.Ordinal))
            && !Uri.UnescapeDataString(uri.AbsolutePath).Contains("..", StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    /// <summary>The one file of an archive that is the chosen subtitle, if the archive is small and plain enough.</summary>
    private (byte[] Data, string Name)? ExtractSingleSafeEntry(byte[] zip, DownloadSelection selection)
    {
        if (_text.Download.Archive is not { } rules)
        {
            return null;
        }

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.Entries.Count == 0 || archive.Entries.Count > rules.MaxEntries)
        {
            return null;
        }

        var limit = _text.Download.MaxBytes;
        var entries = archive.Entries.Where(entry => SupportedFile(entry.FullName)
            && !entry.FullName.Contains('/') && !entry.FullName.Contains('\\')
            && !entry.FullName.Contains("..", StringComparison.Ordinal)
            && entry.Length > 0 && entry.Length <= limit
            && entry.Length <= Math.Max(entry.CompressedLength, 1) * rules.MaxExpansionRatio).ToList();
        var selected = entries.Where(entry => string.Equals(entry.Name, selection.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count != 1)
        {
            return null;
        }

        using var input = selected[0].Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.Length > limit ? null : (output.ToArray(), selected[0].Name);
    }

    private static bool IsZip(byte[] data) => data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4b
        && data[2] == 0x03 && data[3] == 0x04;

    private static string DecodeText(byte[] data) =>
        data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe
            ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
            : Encoding.UTF8.GetString(data).TrimStart('﻿');
}
