using System.Text.Json.Serialization;

namespace MediaEngine.Web.Components.Settings;

/// <summary>One file the browser handed over (dropped or chosen). The bytes stay in the browser; only these facts come to the page.</summary>
public sealed class UploadFileItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Type { get; set; } = string.Empty;

    /// <summary>The kind this browser remembers an unfinished upload of this same name and size was for, or null.</summary>
    public string? ResumeKind { get; set; }

    /// <summary>The library that unfinished upload was for, or null.</summary>
    public string? ResumeLibrary { get; set; }
}

/// <summary>How one file's upload ended, as the browser reports it.</summary>
public sealed class UploadOutcome
{
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("uploadId")] public string? UploadId { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("renamed")] public bool Renamed { get; set; }
    [JsonPropertyName("destinationPath")] public string? DestinationPath { get; set; }

    /// <summary>On a large-upload refusal: the kind's Import sub-folder as the Engine sees it, where the file can be copied instead.</summary>
    [JsonPropertyName("importFolderPath")] public string? ImportFolderPath { get; set; }

    /// <summary>True when the Engine no longer holds the upload, so trying again cannot carry on where it stopped.</summary>
    [JsonPropertyName("closed")] public bool Closed { get; set; }
}

public enum UploadRowState
{
    Starting,
    Uploading,
    Done,
    Refused,
    Failed,
    Notice,

    /// <summary>Above the warning size: waiting for the person to choose Upload anyway or Cancel.</summary>
    NeedsConfirm,
}

/// <summary>One row of the progress list.</summary>
public sealed class UploadRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public UploadFileClass Class { get; init; }
    public string? KindKey { get; set; }
    public string? LibraryId { get; set; }
    public UploadRowState State { get; set; } = UploadRowState.Starting;
    public long Sent { get; set; }
    public string? Message { get; set; }
    public string? LinkHref { get; set; }
    public string? LinkText { get; set; }

    /// <summary>The files dropped or chosen together; a large-upload warning is one box per drop.</summary>
    public Guid DropId { get; init; }

    /// <summary>The page of the kind this row is for, so a "no folder" refusal can link to it.</summary>
    public string? KindAddress { get; set; }

    /// <summary>For a row waiting on the large-upload warning: where the file can be copied instead.</summary>
    public string? ImportFolderPath { get; set; }
    public string? FinalName { get; set; }
    public bool Renamed { get; set; }
    public bool CanRetry { get; set; }
    public bool Resumed { get; set; }

    /// <summary>The last quarter announced to screen readers, so progress is read out in steps rather than every update.</summary>
    public int AnnouncedStep { get; set; }

    public int Percent => Size <= 0 ? 0 : (int)Math.Clamp(Sent * 100 / Size, 0, 100);
}

/// <summary>One question in the file-drop dialog: which kind (and library) a set of dropped files is for.</summary>
public sealed class UploadSection
{
    public required UploadFileClass Class { get; init; }
    public required string Heading { get; init; }
    public required IReadOnlyList<LibraryTypeOption> Kinds { get; init; }
    public required IReadOnlyList<UploadRow> Rows { get; init; }

    /// <summary>The kind chosen so far; null until one is.</summary>
    public string? SelectedKey { get; set; }

    /// <summary>An existing library's id, <see cref="LibraryChoiceOptions.NewChoice"/> for a new custom library, or null.</summary>
    public string? LibraryChoice { get; set; }

    public bool WantsNewLibrary => LibraryChoice == LibraryChoiceOptions.NewChoice;
}
