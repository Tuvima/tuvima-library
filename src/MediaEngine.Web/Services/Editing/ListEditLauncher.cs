using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Editing;

/// <summary>An item a list wants to edit: what the list already knows about the row, so the pop-over can open without going to the item's page.</summary>
/// <param name="EntityId">The row's own id.</param>
/// <param name="Title">The row's name, for the pop-over's header while its fields load.</param>
/// <param name="Kind">What the row is (a movie, an episode, a song); <see cref="DetailEntityType.Work"/> when the list does not know better.</param>
/// <param name="MediaType">The editor media type ("Movies", "TV", "Music", "Books"...).</param>
/// <param name="Context">The surface the list belongs to (Watch, Listen...), which the Engine uses to answer.</param>
public sealed record ListEditTarget(
    Guid EntityId,
    string Title,
    DetailEntityType Kind = DetailEntityType.Work,
    string MediaType = "Books",
    DetailPresentationContext Context = DetailPresentationContext.Default)
{
    /// <summary>A library row whose media type is the free text the list holds ("Movie", "TV Show", "Music"...); the surface follows the media type.</summary>
    public static ListEditTarget ForLibraryRow(Guid entityId, string? title, string? mediaTypeText)
    {
        var mediaType = ListEditLauncher.NormalizeMediaType(mediaTypeText);
        var context = mediaType switch
        {
            "Movies" or "TV" => DetailPresentationContext.Watch,
            "Music" or "Audiobooks" => DetailPresentationContext.Listen,
            "Comics" => DetailPresentationContext.Comics,
            _ => DetailPresentationContext.Read,
        };
        return new ListEditTarget(entityId, string.IsNullOrWhiteSpace(title) ? "This item" : title, DetailEntityType.Work, mediaType, context);
    }
}

/// <summary>
/// How a list opens the one pop-over editor over itself (universal editor v3 D6, WP3): a library row's "..." &gt; Edit, an episode or track
/// row, a Needs Review row's Fix, a library's Identify, the player's lyrics button. Nothing navigates away: the list stays where it is and
/// the layout's pop-over opens over it. The edit permission is the Engine's own answer for that item (one small read of the item's page,
/// the same "Edit" action the item's page offers); the pop-over never opens without it, and <see cref="EditorDeviceRule"/> still gates the
/// launch. The list refreshes when the pop-over closes (<see cref="EditorHost.Closed"/>).
/// </summary>
public sealed class ListEditLauncher
{
    private readonly EditorHost _host;
    private readonly IEngineApiClient _api;
    private readonly DeviceContextService _device;
    private readonly IServiceProvider _services;
    private readonly ILogger<ListEditLauncher> _logger;
    private Guid? _profileId;

    public ListEditLauncher(
        EditorHost host,
        IEngineApiClient api,
        DeviceContextService device,
        IServiceProvider services,
        ILogger<ListEditLauncher> logger)
    {
        _host = host;
        _api = api;
        _device = device;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Opens the pop-over on <paramref name="target"/>. <paramref name="tool"/> null opens Match for an item that is not matched and Details
    /// otherwise; Fix and Identify pass <see cref="EditTool.Match"/>. False when the device or the Engine's answer refuses (the reason is
    /// told in a toast) or the Engine could not be reached.
    /// </summary>
    public async Task<bool> OpenAsync(ListEditTarget target, EditTool? tool = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var binding = await BindAsync(target);
        if (binding is null)
        {
            return false;
        }

        return await _host.OpenAsync(binding, target.EntityId, tool);
    }

    /// <summary>A row's own Edit (a song, an episode): opens the pop-over with that row ticked and the Edit panel open, over the list.</summary>
    public async Task<bool> OpenRowAsync(BulkEditRow row, DetailPresentationContext context = DetailPresentationContext.Default)
    {
        ArgumentNullException.ThrowIfNull(row);
        var target = new ListEditTarget(row.Id, row.Title, row.Kind, row.MediaType ?? "Books", context);
        var binding = await BindAsync(target);
        return binding is not null && await RowEditEntry.StartAsync(_host, binding, row);
    }

    /// <summary>
    /// The editor media type of a library row's own media type text ("Movie", "TV Show", "Music", "Audiobook"...).
    /// </summary>
    public static string NormalizeMediaType(string? mediaType)
    {
        var value = mediaType ?? string.Empty;
        if (value.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return "Movies";
        }

        if (value.Contains("tv", StringComparison.OrdinalIgnoreCase))
        {
            return "TV";
        }

        if (value.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return "Music";
        }

        if (value.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return "Audiobooks";
        }

        return value.Contains("comic", StringComparison.OrdinalIgnoreCase) ? "Comics" : "Books";
    }

    /// <summary>
    /// Makes the binding a list with ticked rows opens the editor through (the bar's Edit, Match or Move on rows that have no page behind
    /// them). The Engine checks every change when it is saved; the editor itself needs only a device that may edit.
    /// </summary>
    public EditorBinding BindRows(string? mediaType, Func<IReadOnlyList<BulkEditRow>> rows, Func<Task>? saved = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var binding = new EditorBinding(_device)
        {
            HasPermission = true,
            ShowHistory = false,
            BulkMediaType = mediaType,
        };
        binding.EntityId = rows().FirstOrDefault()?.Id ?? Guid.Empty;
        binding.Trail = () => rows() is { Count: > 0 } ticked
            ? [new EditTrailItem(ticked[0].Id, ticked[0].Title, "Item")]
            : [new EditTrailItem(Guid.Empty, "These items", "Item")];
        binding.MediaTypes = () => rows()
            .Select(row => row.MediaType ?? mediaType)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty(mediaType ?? string.Empty)
            .Where(type => type.Length > 0)
            .ToList();
        binding.LoadFields = async () =>
        {
            var active = _host.Session.ActiveItem?.EntityId ?? binding.EntityId;
            if (active != Guid.Empty)
            {
                await _host.Mode.Fields.LoadAsync(active, mediaType, null, await ProfileIdAsync());
            }
        };
        binding.OpenItem = async item =>
        {
            await _host.Mode.Fields.LoadAsync(item.EntityId, mediaType, EditTrailKinds.ScopeOf(item.Kind), await ProfileIdAsync());
            return _host.Mode.Fields.Loaded && _host.Mode.Fields.EntityId == item.EntityId;
        };
        binding.Saved = saved;
        return binding;
    }

    // One small read before opening: the Engine's own answer whether this person may edit this item, and where its editable fields live.
    private async Task<EditorBinding?> BindAsync(ListEditTarget target)
    {
        if (target.EntityId == Guid.Empty || EditorDeviceRule.TryRefuse(_device, _services.GetService(typeof(IAppToastService)) as IAppToastService))
        {
            return null;
        }

        DetailPageViewModel? page;
        try
        {
            page = await _api.GetDetailPageAsync(target.Kind, target.EntityId, target.Context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The Engine could not say whether {EntityId} may be edited", target.EntityId);
            page = null;
        }

        var editAction = page?.OverflowActions.FirstOrDefault(action => action.Key is DetailActionKeys.Edit or DetailActionKeys.EditMedia);
        if (page is null || editAction is null || editAction.IsDisabled)
        {
            Tell(editAction?.IsDisabled == true
                ? editAction.Tooltip ?? "Editing is not available right now."
                : "Editing is not available for this item.");
            return null;
        }

        var editorTarget = page.EditorTarget is { } found && Guid.TryParse(found.EntityId, out var foundId)
            ? (Id: foundId, Kind: found.EntityKind, Scope: found.InitialScope)
            : (Id: target.EntityId, Kind: target.Kind.ToString(), Scope: (string?)null);
        var mediaType = target.MediaType;
        var binding = new EditorBinding(_device)
        {
            EntityId = target.EntityId,
            HasPermission = true,
            ShowHistory = true,
            BulkMediaType = mediaType,
            IsUnmatched = page.OverflowActions.Any(action => DetailActionKeys.IsUnmatchedAction(action.Key)),
            Extras = page.Extras,
        };
        binding.Trail = () => [new EditTrailItem(target.EntityId, target.Title, "Item")];
        binding.MediaTypes = () => [mediaType];
        binding.LoadFields = async () =>
        {
            var active = _host.Session.ActiveItem?.EntityId ?? target.EntityId;
            if (active != target.EntityId)
            {
                await _host.Mode.Fields.LoadAsync(active, mediaType, null, await ProfileIdAsync());
            }
            else if (editorTarget.Kind is "Person" or "StructuralCollection" or "Collection")
            {
                await _host.Mode.Fields.LoadSubjectAsync(editorTarget.Id);
            }
            else
            {
                await _host.Mode.Fields.LoadAsync(editorTarget.Id, mediaType, editorTarget.Scope, await ProfileIdAsync());
            }
        };
        binding.OpenItem = async item =>
        {
            await _host.Mode.Fields.LoadAsync(item.EntityId, mediaType, EditTrailKinds.ScopeOf(item.Kind), await ProfileIdAsync());
            return _host.Mode.Fields.Loaded && _host.Mode.Fields.EntityId == item.EntityId;
        };
        return binding;
    }

    private async Task<Guid?> ProfileIdAsync()
    {
        if (_profileId is null && _services.GetService(typeof(UIOrchestratorService)) is UIOrchestratorService orchestrator)
        {
            _profileId = (await orchestrator.GetActiveProfileAsync())?.Id;
        }

        return _profileId;
    }

    private void Tell(string message)
    {
        if (_services.GetService(typeof(IAppToastService)) is IAppToastService toasts)
        {
            toasts.Add(message, AppSeverity.Info);
        }
    }
}
