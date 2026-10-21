using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Pages;

/// <summary>Who is asking for a plugin page, as far as page visibility is concerned.</summary>
internal readonly record struct PluginPageViewer(bool IsAdministrator, bool IsRestrictedProfile);

internal enum PluginPageSubmitOutcome
{
    Saved,
    NotFound,
    Invalid,
    Refused,
}

internal sealed record PluginPageSubmitResult(PluginPageSubmitOutcome Outcome, string? Problem = null, string? RecordKey = null);

/// <summary>
/// Turns the pages plugins declare (WP16) into menu entries and page data. Only a switched-on, approved plugin that holds
/// <c>plugin-pages</c> shows anything, only to the audience the page declares, and a page the viewer may not see answers
/// exactly like a page that does not exist. Data comes from the plugin's own records; the plugin supplies values, never markup.
/// </summary>
internal sealed class PluginPageService(
    PluginCatalog catalog,
    IPluginExecutionContextFactory contexts,
    ILogger<PluginPageService> logger,
    TimeProvider clock)
{
    public const int MaxItems = 500;
    private const int MaxCellLength = 500;
    private const int MaxFormValueLength = 2000;

    /// <summary>Most submissions one form keeps, so visitors cannot use up the plugin's whole record allowance.</summary>
    public const int MaxSubmissionsPerForm = 1000;

    public PluginMenuResponse Menu(PluginPageViewer viewer)
    {
        var response = new PluginMenuResponse();
        foreach (var registration in catalog.List().OrderBy(r => r.Manifest.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var page in VisiblePages(registration, viewer).Where(p => p.MenuLabel is not null))
            {
                response.Entries.Add(new PluginMenuEntryDto
                {
                    PluginId = registration.Manifest.Id,
                    PageKey = page.Key,
                    Label = page.MenuLabel!,
                    PageType = page.Type,
                    PhoneSupported = page.PhoneSupported,
                });
            }
        }

        return response;
    }

    public async Task<PluginPageResponse?> GetAsync(PluginPageViewer viewer, string pluginId, string pageKey, CancellationToken ct)
    {
        if (!TryFind(viewer, pluginId, pageKey, out var registration, out var page))
        {
            return null;
        }

        var response = new PluginPageResponse
        {
            PluginId = registration.Manifest.Id,
            PluginName = registration.Manifest.Name,
            PageKey = page.Key,
            Title = page.Title,
            PageType = page.Type,
            PhoneSupported = page.PhoneSupported,
            Columns = page.Columns.Select(c => new PluginPageColumnDto { Key = c.Key, Label = c.Label }).ToList(),
            FormFields = page.FormFields.Select(f => new PluginPageFormFieldDto { Key = f.Key, Label = f.Label, Kind = f.Kind, Required = f.Required }).ToList(),
            SubmitLabel = page.Type == PluginPageDeclaration.Form ? page.SubmitLabel : "",
        };
        if (page.Type == PluginPageDeclaration.Form)
        {
            return response;
        }

        try
        {
            await using var context = contexts.Create(registration.Manifest.Id, "page");
            var records = page.Type == PluginPageDeclaration.Detail
                ? (await context.Records.GetAsync(page.Collection, page.DetailRecord, ct).ConfigureAwait(false)) is { } one ? [one] : []
                : await context.Records.ListAsync(page.Collection, null, MaxItems + 1, ct).ConfigureAwait(false);
            response.Truncated = records.Count > MaxItems;
            foreach (var record in records.Take(MaxItems))
            {
                if (ToItem(registration.Manifest, page, record) is { } item)
                {
                    response.Items.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PluginLimitExceededException or ArgumentException)
        {
            // A plugin that lost its records permission shows an empty page, not an error.
            logger.LogWarning(ex, "Plugin {PluginId} page {PageKey} could not read its records", registration.Manifest.Id, page.Key);
        }

        if (page.Type == PluginPageDeclaration.Guide)
        {
            response.Items = response.Items
                .OrderBy(i => i.Group, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(i => i.StartsAt ?? DateTimeOffset.MaxValue)
                .ToList();
        }

        return response;
    }

    public async Task<PluginPageSubmitResult> SubmitAsync(
        PluginPageViewer viewer,
        string pluginId,
        string pageKey,
        IReadOnlyDictionary<string, string> values,
        CancellationToken ct)
    {
        if (!TryFind(viewer, pluginId, pageKey, out var registration, out var page) || page.Type != PluginPageDeclaration.Form)
        {
            return new PluginPageSubmitResult(PluginPageSubmitOutcome.NotFound);
        }

        var saved = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in page.FormFields)
        {
            values.TryGetValue(field.Key, out var raw);
            var text = raw?.Trim() ?? "";
            if (text.Length > MaxFormValueLength)
            {
                return Invalid($"'{field.Label}' is too long.");
            }

            if (text.Length == 0 && field.Kind == "yes_no")
            {
                // A switch left alone is off.
                saved[field.Key] = false;
                continue;
            }

            if (text.Length == 0)
            {
                if (field.Required)
                {
                    return Invalid($"'{field.Label}' is needed.");
                }

                saved[field.Key] = null;
                continue;
            }

            switch (field.Kind)
            {
                case "number":
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    {
                        return Invalid($"'{field.Label}' must be a number.");
                    }

                    saved[field.Key] = number;
                    break;
                case "yes_no":
                    if (!bool.TryParse(text, out var flag))
                    {
                        return Invalid($"'{field.Label}' must be yes or no.");
                    }

                    saved[field.Key] = flag;
                    break;
                default:
                    saved[field.Key] = text;
                    break;
            }
        }

        var now = clock.GetUtcNow();
        saved["submitted_at"] = now.ToString("O", CultureInfo.InvariantCulture);
        var recordKey = $"submission-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            await using var context = contexts.Create(registration.Manifest.Id, "page-submit");
            var kept = await context.Records.ListAsync(page.Collection, "submission-", MaxSubmissionsPerForm, ct).ConfigureAwait(false);
            if (kept.Count >= MaxSubmissionsPerForm)
            {
                return new PluginPageSubmitResult(PluginPageSubmitOutcome.Refused, "This form has reached the most entries it keeps.");
            }

            await context.Records.PutAsync(page.Collection, recordKey, JsonSerializer.Serialize(saved), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PluginLimitExceededException)
        {
            logger.LogWarning(ex, "Plugin {PluginId} form {PageKey} could not be saved", registration.Manifest.Id, page.Key);
            return new PluginPageSubmitResult(PluginPageSubmitOutcome.Refused, "This plugin cannot keep any more entries right now.");
        }

        return new PluginPageSubmitResult(PluginPageSubmitOutcome.Saved, RecordKey: recordKey);

        static PluginPageSubmitResult Invalid(string problem) => new(PluginPageSubmitOutcome.Invalid, problem);
    }

    private bool TryFind(PluginPageViewer viewer, string pluginId, string pageKey, out PluginRegistration registration, out PluginPageDeclaration page)
    {
        registration = catalog.Get(pluginId)!;
        page = null!;
        if (registration is null)
        {
            return false;
        }

        var found = VisiblePages(registration, viewer).FirstOrDefault(p => string.Equals(p.Key, pageKey, StringComparison.Ordinal));
        if (found is null)
        {
            return false;
        }

        page = found;
        return true;
    }

    private static IEnumerable<PluginPageDeclaration> VisiblePages(PluginRegistration registration, PluginPageViewer viewer)
    {
        var permissions = registration.Manifest.Permissions;
        if (!registration.Enabled
            || registration.LoadError is not null
            || !permissions.Contains(PluginPermissionIds.PluginPages, StringComparer.Ordinal)
            || !permissions.Contains(PluginPermissionIds.PluginRecords, StringComparer.Ordinal))
        {
            return [];
        }

        return PluginPageDeclarations.Read(registration.Manifest).Pages.Where(page => page.Audience switch
        {
            PluginPageAudience.Administrators => viewer.IsAdministrator,
            PluginPageAudience.Adults => !viewer.IsRestrictedProfile,
            _ => true,
        });
    }

    private static PluginPageItemDto? ToItem(PluginManifest manifest, PluginPageDeclaration page, PluginRecord record)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(record.Json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var item = new PluginPageItemDto
            {
                Id = record.Key,
                Title = Cell(root, page.TitleField) ?? record.Key,
                Subtitle = Cell(root, page.SubtitleField),
                Group = Cell(root, page.GroupField) ?? "",
                StartsAt = Time(root, page.StartsAtField),
                EndsAt = Time(root, page.EndsAtField),
                ImageUrl = Image(manifest, Cell(root, page.ImageField)),
            };
            if (page.Type == PluginPageDeclaration.Guide && (item.StartsAt is null || item.EndsAt is null || item.EndsAt <= item.StartsAt))
            {
                return null;
            }

            foreach (var column in page.Columns)
            {
                item.Fields[column.Key] = Cell(root, column.Key) ?? "";
            }

            return item;
        }
    }

    private static string? Cell(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            _ => null,
        };
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length <= MaxCellLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[MaxCellLength - 1]) ? MaxCellLength - 1 : MaxCellLength;
        return text[..cut];
    }

    private static DateTimeOffset? Time(JsonElement root, string property) =>
        Cell(root, property) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    // A picture is fetched by the viewer's browser, so it is only shown from an https host the plugin declared in its network list.
    private static string? Image(PluginManifest manifest, string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
        {
            return null;
        }

        var allowed = manifest.Network.Any(destination => PluginNetworkRules.HostMatches(destination.Host, uri.IdnHost));
        return allowed ? uri.AbsoluteUri : null;
    }
}
