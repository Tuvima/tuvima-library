using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>Whether saving an edit also updates the files, as far as the Dashboard can tell.</summary>
public enum FileWriteState
{
    /// <summary>The setting could not be read or no library holds the item's type, so nothing is claimed.</summary>
    Unknown,

    On,

    Off,
}

/// <summary>
/// The one "Save changes into the files" setting as the editor shows it (universal editor spec D15): a library's own switch in
/// Settings, under the Engine's overall write-back switch. The editor only ever reads it; it is changed in Settings.
/// </summary>
public readonly record struct FileWriteSetting(FileWriteState State)
{
    public static FileWriteSetting Unknown { get; } = new(FileWriteState.Unknown);

    public bool IsOn => State == FileWriteState.On;

    /// <summary>A save may skip the files for this once only when the library would otherwise update them.</summary>
    public bool CanOverride => IsOn;

    /// <summary>Where the status line sends the person to change the setting.</summary>
    public const string SettingsHref = "/settings/libraries";

    /// <summary>The words of the status line, identical wherever the editor shows it.</summary>
    public string? Text => State switch
    {
        FileWriteState.On => "On",
        FileWriteState.Off => "Off",
        _ => null,
    };
}

/// <summary>Works out the <see cref="FileWriteSetting"/> for the media types being edited. Pure: no calls to the Engine.</summary>
public static class FileWriteSettingResolver
{
    public static FileWriteSetting Resolve(
        LibrariesConfigurationDto? libraries,
        FileWriteScheduleDto? schedule,
        IReadOnlyCollection<string> mediaTypes)
    {
        ArgumentNullException.ThrowIfNull(mediaTypes);
        if (schedule is null)
        {
            return FileWriteSetting.Unknown;
        }

        if (!schedule.Enabled)
        {
            return new FileWriteSetting(FileWriteState.Off);
        }

        if (libraries is null || mediaTypes.Count == 0)
        {
            return FileWriteSetting.Unknown;
        }

        var held = libraries.Libraries
            .Where(library => library.MediaTypes.Any(held => mediaTypes.Contains(held, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (held.Count == 0)
        {
            return FileWriteSetting.Unknown;
        }

        // Several types in one save (rare): the files are updated for the ones whose library says so.
        return new FileWriteSetting(held.Any(library => library.OrganizationPolicy.WriteTags) ? FileWriteState.On : FileWriteState.Off);
    }
}

/// <summary>Reads the Engine's answers and resolves them. A failed read is Unknown, so the editor never claims something it does not know.</summary>
public static class FileWriteSettingReader
{
    public static async Task<FileWriteSetting> ReadAsync(
        IEngineApiClient api,
        ILogger logger,
        IReadOnlyCollection<string> mediaTypes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var schedule = await api.GetFileWriteScheduleAsync(ct);
            var libraries = schedule is { Enabled: true } ? await api.GetLibrariesAsync(ct) : null;
            return FileWriteSettingResolver.Resolve(libraries, schedule, mediaTypes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The file write setting could not be read for Review changes");
            return FileWriteSetting.Unknown;
        }
    }
}
