using MediaEngine.Api.Models;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.ReadServices;

public static class SystemViewGroupIdentity
{
    public static Guid CreateId(ContentGroupDto group, string? mediaType, string? groupField)
    {
        var identity = BuildIdentity(group, mediaType, groupField);
        return Hashing.DeterministicGuid($"{Normalize(mediaType)}|{Normalize(groupField)}|{identity}");
    }

    private static string BuildIdentity(ContentGroupDto group, string? mediaType, string? groupField)
    {
        if (string.Equals(groupField, "series", StringComparison.OrdinalIgnoreCase)
            && group.RootWorkId is Guid rootWorkId && rootWorkId != Guid.Empty)
        {
            return rootWorkId.ToString("D");
        }
        var name = Normalize(group.DisplayName);
        if (string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}|{Normalize(group.Creator)}";
        }

        return string.Join("|",
            name,
            Normalize(group.Creator),
            Normalize(group.Network),
            Normalize(group.Year));
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "(blank)"
            : value.Trim().ToLowerInvariant();
}
