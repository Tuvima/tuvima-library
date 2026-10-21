using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Events;

namespace MediaEngine.Api.Services.Events;

public sealed class ApplicationEventRegistry(IPermissionRegistry permissions)
{
    // Webhooks, plugin subscriptions and notification routes all read the one list in LibraryEvents.
    private static readonly ApplicationEventDefinition[] Values =
    [
        .. LibraryEvents.All.Select(entry => new ApplicationEventDefinition(entry.EventType, entry.ReadPermission, entry.IsLibraryScoped, entry.IsProfileScoped)),
    ];

    private readonly IReadOnlyDictionary<string, ApplicationEventDefinition> _byType = Values.ToDictionary(value => value.EventType, StringComparer.Ordinal);

    public IReadOnlyList<ApplicationEventDefinition> GetAll() => Values;

    /// <summary>
    /// True when the Library has an event of this type. Webhooks and plugin event subscriptions both draw on this one list,
    /// so an event added to it is available to both.
    /// </summary>
    public static bool IsKnownType(string? eventType) =>
        eventType is not null && Array.Exists(Values, value => string.Equals(value.EventType, eventType, StringComparison.Ordinal));

    /// <summary>True for the events that say what people watch, read and listen to (<c>playback.*</c>).</summary>
    public static bool IsWatchActivity(string eventType) =>
        eventType.StartsWith("playback.", StringComparison.Ordinal);

    public IReadOnlyList<string> ListAvailableTypes() =>
        Values.Where(value =>
                permissions.TryGet(ApplicationPermissionIds.EventsSubscribe, out var subscribe) && subscribe.IsAvailable &&
                permissions.TryGet(value.ReadPermission, out var read) && read.IsAvailable &&
                (!value.IsLibraryScoped || permissions.TryGet(ApplicationPermissionIds.LibraryRead, out var library) && library.IsAvailable))
            .Select(value => value.EventType).Order(StringComparer.Ordinal).ToArray();

    public bool TryGet(string eventType, out ApplicationEventDefinition definition) =>
        _byType.TryGetValue(eventType, out definition!);
}
