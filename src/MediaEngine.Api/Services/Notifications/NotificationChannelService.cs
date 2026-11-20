using MediaEngine.Contracts.Notifications;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>
/// Adds, changes and removes the channels people set up in Settings (notifications and delivery spec WP2). Each channel is one
/// of the types in <see cref="ChannelTypeCatalogue"/>; links and tokens go to the secrets store and never come back out.
/// </summary>
internal sealed class NotificationChannelService(
    NotificationChannelStore store,
    ChannelTypeCatalogue types,
    NotificationService notifications)
{
    public const int MaxChannels = 30;
    private const int MaxNameLength = 60;
    private const int MaxValueLength = 1000;

    public NotificationChannelTypesResponse GetTypes() => new()
    {
        Types =
        [
            .. types.All.Select(type => new NotificationChannelTypeDto
            {
                Id = type.Id,
                Name = type.Name,
                Advanced = type.Advanced,
                Cost = type.Cost,
                Effort = type.Effort,
                Summary = type.Summary,
                Help = type.Help,
                WhatLeaves = type.WhatLeaves,
                Fields =
                [
                    .. type.Fields.Select(field => new NotificationChannelFieldDto
                    {
                        Key = field.Key,
                        Label = field.Label,
                        Help = field.Help,
                        Kind = field.Kind,
                        Secret = field.Secret,
                        Required = field.Required,
                        Placeholder = field.Placeholder,
                        Default = field.Default,
                        SuggestRandom = field.SuggestRandom,
                    }),
                ],
            }),
        ],
    };

    public async Task<NotificationChannelDetailDto?> GetAsync(string id, CancellationToken ct)
    {
        var channel = await store.FindAsync(id, ct).ConfigureAwait(false);
        return channel is null
            ? null
            : new NotificationChannelDetailDto
            {
                Id = channel.Id,
                Type = channel.Type,
                Name = channel.Name,
                Values = new Dictionary<string, string>(channel.Fields, StringComparer.Ordinal),
                SecretsSet = [.. channel.Secrets.Keys],
            };
    }

    /// <summary>Creates a channel. Returns the new channel's id, or a plain-words problem.</summary>
    public async Task<(string? Id, string? Problem)> CreateAsync(SaveNotificationChannelRequest request, CancellationToken ct)
    {
        if (types.Find(request.Type) is not { } type)
        {
            return (null, "That kind of channel is not available.");
        }

        if ((await store.ListAsync(ct).ConfigureAwait(false)).Count >= MaxChannels)
        {
            return (null, "That is a lot of channels. Remove one before adding another.");
        }

        var channel = new StoredChannelInstance { Id = $"channel:{Guid.NewGuid():N}", Type = type.Id };
        var problem = Apply(channel, type, request, existing: null);
        if (problem is not null)
        {
            return (null, problem);
        }

        await store.SaveAsync(channel, ct).ConfigureAwait(false);
        return (channel.Id, null);
    }

    /// <summary>Changes a channel. Returns a plain-words problem, or null when it was saved.</summary>
    public async Task<string?> UpdateAsync(string id, SaveNotificationChannelRequest request, CancellationToken ct)
    {
        var existing = await store.FindAsync(id, ct).ConfigureAwait(false);
        if (existing is null || types.Find(existing.Type) is not { } type)
        {
            return "That channel was not found.";
        }

        var channel = new StoredChannelInstance
        {
            Id = existing.Id,
            Type = existing.Type,
            Fields = new Dictionary<string, string>(existing.Fields, StringComparer.Ordinal),
            Secrets = new Dictionary<string, string>(existing.Secrets, StringComparer.Ordinal),
        };
        var problem = Apply(channel, type, request, existing);
        if (problem is not null)
        {
            return problem;
        }

        await store.SaveAsync(channel, ct).ConfigureAwait(false);
        return null;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        if (!await store.RemoveAsync(id, ct).ConfigureAwait(false))
        {
            return false;
        }

        await notifications.ForgetChannelAsync(id, ct).ConfigureAwait(false);
        return true;
    }

    private string? Apply(StoredChannelInstance channel, ChannelTypeTemplate type, SaveNotificationChannelRequest request, StoredChannelInstance? existing)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        channel.Name = name.Length == 0 ? type.Name : name;
        if (channel.Name.Length > MaxNameLength)
        {
            return $"Keep the name under {MaxNameLength} letters.";
        }

        foreach (var field in type.Fields)
        {
            request.Values.TryGetValue(field.Key, out var raw);
            var value = raw?.Trim();
            var keepsSaved = value is null && existing is not null && (field.Secret ? existing.Secrets.ContainsKey(field.Key) : existing.Fields.ContainsKey(field.Key));
            if (keepsSaved)
            {
                continue;
            }

            value = string.IsNullOrEmpty(value) ? field.Default : value;
            if (string.IsNullOrEmpty(value))
            {
                if (field.Required)
                {
                    return $"{field.Label} is needed.";
                }

                channel.Fields.Remove(field.Key);
                channel.Secrets.Remove(field.Key);
                continue;
            }

            var problem = ValidateValue(field, value);
            if (problem is not null)
            {
                return problem;
            }

            if (field.Secret)
            {
                channel.Secrets[field.Key] = store.Protect(value);
                channel.Fields.Remove(field.Key);
            }
            else
            {
                channel.Fields[field.Key] = value;
                channel.Secrets.Remove(field.Key);
            }
        }

        return null;
    }

    internal static string? ValidateValue(ChannelFieldTemplate field, string value)
    {
        if (value.Length > MaxValueLength || value.Any(char.IsControl))
        {
            return $"{field.Label} is not valid.";
        }

        if (field.Kind != "url")
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return $"{field.Label} must be a full web address starting with https://.";
        }

        if (field.HostSuffixes.Count > 0
            && (uri.Scheme != Uri.UriSchemeHttps
                || !field.HostSuffixes.Any(suffix => uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))))
        {
            return $"{field.Label} should be the link {string.Join(" or ", field.HostSuffixes)} gave you.";
        }

        return null;
    }
}
