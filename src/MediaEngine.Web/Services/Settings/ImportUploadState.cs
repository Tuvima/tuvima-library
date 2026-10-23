using MediaEngine.Contracts.Imports;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Settings;

/// <summary>One box warning that some files of one drop are large (Library setup D44): the files, and the folders they could be copied into instead.</summary>
public sealed class ImportUploadWarning
{
    public required Guid DropId { get; init; }
    public required IReadOnlyList<UploadRow> Rows { get; init; }

    /// <summary>Each distinct folder the Engine named, in the order the files came.</summary>
    public IReadOnlyList<string> Paths => [.. Rows.Select(row => row.ImportFolderPath).OfType<string>().Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// The files being added to the Import folder, kept for the signed-in session (Library setup D44). The uploads themselves run in the
/// browser (<c>wwwroot/js/import-upload.js</c>) and carry on while the person moves between Libraries pages; this holds the list so
/// every Libraries page shows the same progress when it opens, and receives the browser's progress and results.
/// </summary>
public sealed class ImportUploadState : IDisposable
{
    private readonly IAppToastService _toasts;
    private readonly List<UploadRow> _rows = [];
    private DotNetObjectReference<ImportUploadState>? _reference;

    public ImportUploadState(IAppToastService toasts)
    {
        ArgumentNullException.ThrowIfNull(toasts);
        _toasts = toasts;
    }

    /// <summary>Raised whenever the list or a row changes, so pages can draw again.</summary>
    public event Action? Changed;

    /// <summary>What a screen reader is told; it changes on a state change or each quarter of a file, not on every update.</summary>
    public string LiveText { get; private set; } = string.Empty;

    /// <summary>The rows to draw as the progress list (those waiting on the large-upload warning are in <see cref="Warnings"/> instead).</summary>
    public IReadOnlyList<UploadRow> Listed => [.. _rows.Where(row => row.State != UploadRowState.NeedsConfirm)];

    public IReadOnlyList<ImportUploadWarning> Warnings =>
    [
        .. _rows.Where(row => row.State == UploadRowState.NeedsConfirm)
            .GroupBy(row => row.DropId)
            .Select(group => new ImportUploadWarning { DropId = group.Key, Rows = [.. group] }),
    ];

    public bool HasRows => _rows.Count > 0;

    /// <summary>What the browser calls back into: progress and results.</summary>
    public DotNetObjectReference<ImportUploadState> Reference => _reference ??= DotNetObjectReference.Create(this);

    public void Add(UploadRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _rows.Add(row);
        Changed?.Invoke();
    }

    public void Remove(UploadRow row)
    {
        if (_rows.Remove(row))
        {
            Changed?.Invoke();
        }
    }

    public UploadRow? Find(string id) => _rows.FirstOrDefault(row => row.Id == id);

    public void Announce(string text)
    {
        LiveText = text;
        Changed?.Invoke();
    }

    /// <summary>Tells the pages a row changed.</summary>
    public void Touch() => Changed?.Invoke();

    [JSInvokable]
    public void OnUploadProgress(string id, long sent, long total)
    {
        if (Find(id) is not { } row)
        {
            return;
        }

        row.State = UploadRowState.Uploading;
        row.Sent = sent;
        var step = total > 0 ? Math.Min(100, (int)(sent * 100 / total)) / 25 : 0;
        if (step > row.AnnouncedStep)
        {
            row.AnnouncedStep = step;
            LiveText = $"{row.Name}: {step * 25} percent";
        }

        Changed?.Invoke();
    }

    [JSInvokable]
    public void OnUploadFinished(string id, UploadOutcome outcome)
    {
        if (Find(id) is not { } row)
        {
            return;
        }

        ApplyOutcome(row, outcome);
        LiveText = $"{row.Name}: {StateText(row)}";
        Changed?.Invoke();
    }

    /// <summary>Shows how a file's upload ended.</summary>
    public void ApplyOutcome(UploadRow row, UploadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(outcome);
        switch (outcome.State)
        {
            case "completed":
                row.State = UploadRowState.Done;
                row.Sent = row.Size;
                row.FinalName = outcome.FileName;
                row.Renamed = outcome.Renamed;
                _toasts.Add($"{outcome.FileName ?? row.Name} was added to the Import folder.", AppSeverity.Success);
                break;
            case "refused" when outcome.Code == ImportUploadRefusalCodes.LargeUploadNeedsConfirm:
                // Not a failure: the person decides in the warning box. Nothing was created, so nothing is shown in the list.
                row.State = UploadRowState.NeedsConfirm;
                row.ImportFolderPath = outcome.ImportFolderPath;
                break;
            case "refused":
                var (text, href, linkText) = UploadRefusalMessages.Describe(outcome.Code, outcome.Message, row.KindAddress);
                row.State = UploadRowState.Refused;
                row.Message = text;
                row.LinkHref = href;
                row.LinkText = linkText;
                break;
            case "cancelled":
                _rows.Remove(row);
                break;
            default:
                row.State = UploadRowState.Failed;
                row.Message = string.IsNullOrWhiteSpace(outcome.Message) ? UploadRefusalMessages.Fallback : outcome.Message;
                row.CanRetry = !outcome.Closed;
                break;
        }
    }

    public static string StateText(UploadRow row) => row.State switch
    {
        UploadRowState.Done => "added to the Import folder",
        UploadRowState.Refused or UploadRowState.Failed or UploadRowState.Notice => row.Message ?? "not added",
        UploadRowState.NeedsConfirm => "large file, waiting for your answer",
        UploadRowState.Uploading => $"{row.Percent} percent",
        _ => "starting",
    };

    public void Dispose() => _reference?.Dispose();
}
