namespace MediaEngine.Ingestion.Services.ImportUploads;

/// <summary>
/// The in-memory guard around one upload. Pieces being written hold a writer slot; complete, cancel and the abandoned-upload sweep take
/// the exclusive lock, then <see cref="CloseWritersAsync"/> stops every piece still being written and waits for it to let go of the
/// working file, so the file can be moved or deleted (Windows refuses to delete a file that is still open).
/// </summary>
internal sealed class ImportUploadGate
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private int _writers;
    private bool _closing;
    private TaskCompletionSource? _drained;

    /// <summary>Held by complete, cancel and the sweep, one at a time, for this upload.</summary>
    public SemaphoreSlim Exclusive { get; } = new(1, 1);

    /// <summary>Takes a writer slot. False once the upload is closing: no more pieces are accepted. The token fires when the writers are told to stop.</summary>
    public bool TryEnterWriter(out CancellationToken stopToken)
    {
        lock (_sync)
        {
            if (_closing)
            {
                stopToken = CancellationToken.None;
                return false;
            }

            _writers++;
            stopToken = _stop.Token;
            return true;
        }
    }

    public void ExitWriter()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _writers--;
            if (_writers == 0 && _drained is not null)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    /// <summary>Refuses new pieces, tells the ones in flight to stop and returns once every one of them has let go of the file.</summary>
    public async Task CloseWritersAsync()
    {
        Task wait;
        lock (_sync)
        {
            _closing = true;
            if (_writers == 0)
            {
                return;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _drained.Task;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await wait.ConfigureAwait(false);
    }
}
