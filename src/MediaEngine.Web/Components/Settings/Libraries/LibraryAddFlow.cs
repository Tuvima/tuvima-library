using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The one place Add library is started from (the rail's Add links, the overview's button and the empty-area state), in Settings and in
/// first-run setup alike (Libraries redesign D8, D13).
/// </summary>
public static class LibraryAddFlow
{
    /// <summary>Opens the Add library dialog on the given area (Watch when empty). <paramref name="onCreated"/> gets the new library's Id.</summary>
    public static async Task OpenAsync(
        IAppDialogService dialogs,
        LibrariesConfigurationDto configuration,
        LibraryKindCatalog kinds,
        string? areaKey,
        bool vaultVisible,
        bool setup,
        string? setupSession,
        Func<string, Task> onCreated)
    {
        var parameters = new AppDialogParameters<CreateLibraryDialog>
        {
            { component => component.Configuration, configuration },
            { component => component.Kinds, kinds },
            { component => component.StartAreaKey, string.IsNullOrWhiteSpace(areaKey) ? "watch" : areaKey },
            { component => component.VaultVisible, vaultVisible },
            { component => component.OpenCreated, !setup },
            { component => component.Simple, setup },
            { component => component.SetupSession, setupSession },
            { component => component.OnCreated, onCreated },
        };
        await dialogs.ShowAsync<CreateLibraryDialog>(string.Empty, parameters, CreateLibraryDialog.DialogOptions);
    }
}
