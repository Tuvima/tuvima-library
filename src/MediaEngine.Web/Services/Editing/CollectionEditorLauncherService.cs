using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

public sealed class CollectionEditorLauncherService
{
    private readonly IAppDialogService _dialogService;
    private readonly AdministratorSurfaceAccessService? _administratorAccess;

    public CollectionEditorLauncherService(IAppDialogService dialogService, AdministratorSurfaceAccessService? administratorAccess = null)
    {
        _dialogService = dialogService;
        _administratorAccess = administratorAccess;
    }

    public CollectionEditorInlineSession BeginInline(CollectionEditorLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EditingCollection is null)
        {
            throw new InvalidOperationException("Inline collection editing requires an existing collection.");
        }

        return new CollectionEditorInlineSession(request);
    }

    public async Task<bool> OpenAsync(CollectionEditorLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isPlaylist = request.Kind == ContainerEditorKind.Playlist;

        if (!isPlaylist && _administratorAccess is not null && !await _administratorAccess.EnsureUnlockedAsync())
        {
            return false;
        }

        var isCollectionEditor = !isPlaylist;
        var dialog = await _dialogService.ShowAsync<CollectionEditorShell>(
            EditDialogTitleFor(request),
            new AppDialogParameters
            {
                { nameof(CollectionEditorShell.Request), request },
            },
            new AppDialogOptions
            {
                CloseButton = false,
                NoHeader = true,
                MaxWidth = AppMaxWidth.ExtraLarge,
                FullWidth = true,
                BackdropClick = false,
                CloseOnEscapeKey = true,
            });

        if (dialog is null)
        {
            return false;
        }

        var result = await dialog.Result;
        return result is not null && !result.Canceled;
    }

    private static string DialogTitleFor(CollectionEditorLaunchRequest request) =>
        request.Kind switch
        {
            ContainerEditorKind.Playlist => "New Playlist",
            ContainerEditorKind.Gallery => "New Gallery",
            _ => "New Collection",
        };

    private static string EditDialogTitleFor(CollectionEditorLaunchRequest request) =>
        request.Kind switch
        {
            ContainerEditorKind.Playlist => "Edit Playlist",
            ContainerEditorKind.Gallery => "Edit Gallery",
            _ => "Edit Collection",
        };
}

public sealed class CollectionEditorInlineSession
{
    internal CollectionEditorInlineSession(CollectionEditorLaunchRequest request)
    {
        Request = request;
    }

    public CollectionEditorLaunchRequest Request { get; }
}
