using System.Collections.Concurrent;
using System.Text;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// A pasted address that cannot be used, with a plain sentence for the person (Collections and lists spec 4h). When the fix is in
/// Settings, the sentence comes with the link that opens the Plugins page.
/// </summary>
public sealed class WebListRefusedException(string message, string? linkHref = null, string? linkLabel = null)
    : ListFileNotUnderstoodException(message)
{
    public string? LinkHref { get; } = linkHref;

    public string? LinkLabel { get; } = linkLabel;
}

/// <summary>
/// Turns what a person pasted into a list (Collections and lists spec 4b, 4d, 4h). Text that is a single web address is looked up
/// among the web sources; anything else is a file or a plain list and is left to the file reader. A source that is switched off,
/// or that needs a key that is not set, refuses its addresses with the sentence the spec gives, and only its own addresses:
/// file import and every other source carry on. A site that is down gives a list with nothing in it yet and "will try again".
/// </summary>
public sealed class WebListResolver
{
    /// <summary>How long a list that was just read is kept, so a preview, a hand-fix and the save do not each ask the site again.</summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    private const int MaximumAddressLength = 2000;
    private const string PluginsRoute = "/settings/plugins";

    private readonly IReadOnlyList<IWebListSource> _sources;
    private readonly IConfigurationLoader _configuration;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ParsedList List)> _recent = new(StringComparer.Ordinal);

    public WebListResolver(IEnumerable<IWebListSource> sources, IConfigurationLoader configuration, TimeProvider? time = null)
    {
        _sources = sources.ToList();
        _configuration = configuration;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The sites a list address can come from, in the words used when one is not recognised.</summary>
    public string AcceptedSites => string.Join(", ", _sources.Select(source => source.DisplayName.Replace(" lists", string.Empty, StringComparison.Ordinal)
        .Replace(" collections", string.Empty, StringComparison.Ordinal)));

    /// <summary>The address, when the bytes are a single web address (a pasted link) and not a file.</summary>
    public static bool TryReadAddress(byte[] bytes, out Uri? address)
    {
        address = null;
        if (bytes.Length is 0 or > MaximumAddressLength || (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(bytes).Trim().TrimStart('﻿').Trim();
        if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal) || text.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        address = uri;
        return true;
    }

    /// <summary>
    /// Reads the list a pasted address names. Returns null when the bytes are not an address at all.
    /// </summary>
    /// <exception cref="WebListRefusedException">The address cannot be used; the message says why.</exception>
    public async Task<ParsedList?> ResolveAsync(byte[] bytes, CancellationToken ct)
    {
        if (!TryReadAddress(bytes, out var uri) || uri is null)
        {
            return null;
        }

        var source = _sources.FirstOrDefault(candidate => candidate.Recognise(uri) is not null);
        if (source is null)
        {
            throw new WebListRefusedException(
                $"Library reads lists from {AcceptedSites} addresses. For IMDb, Letterboxd or Goodreads, export the list as a file and drop it here instead.");
        }

        var address = source.Recognise(uri)!;
        if (_configuration.LoadProvider(source.ProviderName) is not { Enabled: true })
        {
            throw new WebListRefusedException(
                $"{source.DisplayName} are switched off. Turn the source on in Settings → Plugins.", PluginsRoute, "Open Plugins");
        }

        var now = _time.GetUtcNow();
        if (_recent.TryGetValue(address.Address, out var recent) && now - recent.At < KeepFor)
        {
            return recent.List;
        }

        var fetch = await source.FetchAsync(address, ct).ConfigureAwait(false);
        switch (fetch.Failure)
        {
            case WebListFailure.None when fetch.List is { } list:
                _recent[address.Address] = (now, list);
                Trim(now);
                return list;
            case WebListFailure.KeyMissing:
                throw new WebListRefusedException(
                    $"{source.DisplayName} need your own key. Add it in Settings → Plugins.", PluginsRoute, "Open Plugins");
            case WebListFailure.KeyRejected:
                throw new WebListRefusedException(
                    $"{source.DisplayName} did not accept the key. Check it in Settings → Plugins.", PluginsRoute, "Open Plugins");
            case WebListFailure.SignInNeeded:
                throw new WebListRefusedException(
                    $"That list is private or your own, so it can only be read when you are signed in to {SiteOf(source)}. Library cannot sign in there yet: make the list public and paste its public address, or export it as a file and drop that here.");
            case WebListFailure.NotFound:
                throw new WebListRefusedException(
                    $"Couldn't find that list. It may be private, or the address may be wrong. Only public lists can be read.");
            default:
                // The site did not answer: the collection is saved with nothing in it yet and Library tries again.
                return new ParsedList
                {
                    SourceKind = address.Kind,
                    SourceAddress = address.Address,
                    WillTryAgain = true,
                };
        }
    }

    private static string SiteOf(IWebListSource source) =>
        source.DisplayName.Replace(" lists", string.Empty, StringComparison.Ordinal).Replace(" collections", string.Empty, StringComparison.Ordinal);

    private void Trim(DateTimeOffset now)
    {
        foreach (var (key, value) in _recent)
        {
            if (now - value.At >= KeepFor)
            {
                _recent.TryRemove(key, out _);
            }
        }
    }
}
