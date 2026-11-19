using System.Security.Cryptography;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Faces;

/// <summary>Where the two face models are and whether they are ready (spec view-people-search WP6). Shown in Tools.</summary>
public enum FaceModelState
{
    /// <summary>Not downloaded yet.</summary>
    Missing,

    /// <summary>A download is running.</summary>
    Downloading,

    /// <summary>The file is there and matches its pinned checksum.</summary>
    Ready,

    /// <summary>The last download failed or the file did not match its checksum.</summary>
    Problem,
}

/// <summary>One face model as the Tools page shows it.</summary>
public sealed record FaceModelStatus(string FileName, FaceModelState State, string? Reason);

/// <summary>
/// The two face models, kept in <c>&lt;data&gt;/tools/faces/</c>: found, verified and fetched once. The files are never in the repository or the
/// build output. A file counts as ready only when its SHA-256 equals the pinned value, so a corrupt or swapped model is never run.
/// </summary>
public sealed class FaceModelProvider(
    IConfigurationLoader configuration,
    AssetPathService paths,
    IToolDownloadSource source,
    ILogger<FaceModelProvider> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (FaceModelState State, string? Reason)> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder holding the downloaded models.</summary>
    public string Directory => Path.Combine(paths.DataRoot, "tools", "faces");

    /// <summary>The full path of a model file (it may not exist yet).</summary>
    public string PathOf(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>True when both models are downloaded and verified.</summary>
    public bool AreReady => Statuses().All(status => status.State == FaceModelState.Ready);

    /// <summary>The state of each pinned model.</summary>
    public IReadOnlyList<FaceModelStatus> Statuses()
    {
        var models = configuration.LoadToolDownloads().FaceModels;
        var result = new List<FaceModelStatus>(models.Count);
        foreach (var model in models)
        {
            lock (_gate)
            {
                if (_states.TryGetValue(model.Name, out var known) && known.State is FaceModelState.Downloading or FaceModelState.Problem)
                {
                    result.Add(new FaceModelStatus(model.Name, known.State, known.Reason));
                    continue;
                }
            }

            result.Add(new FaceModelStatus(model.Name, IsVerified(model) ? FaceModelState.Ready : FaceModelState.Missing, null));
        }

        return result;
    }

    /// <summary>Fetches every model that is missing or does not match its checksum. Safe to call again; each file is fetched once.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        var models = configuration.LoadToolDownloads().FaceModels;
        foreach (var model in models)
        {
            if (IsVerified(model))
            {
                continue;
            }

            await DownloadAsync(model, ct).ConfigureAwait(false);
        }
    }

    private bool IsVerified(DownloadFile model)
    {
        var path = PathOf(model.Name);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "The face model {File} could not be read.", model.Name);
            return false;
        }
    }

    private async Task DownloadAsync(DownloadFile model, CancellationToken ct)
    {
        SetState(model.Name, FaceModelState.Downloading, null);
        var temporary = PathOf(model.Name) + ".part";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await using (var download = await source.OpenAsync(new Uri(model.Url), ct).ConfigureAwait(false))
            await using (var file = File.Create(temporary))
            {
                await download.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            string hash;
            await using (var written = File.OpenRead(temporary))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(written, ct).ConfigureAwait(false));
            }

            if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The download did not match its recorded checksum.");
            }

            File.Move(temporary, PathOf(model.Name), overwrite: true);
            lock (_gate)
            {
                _states.Remove(model.Name);
            }

            logger.LogInformation("The face model {File} was downloaded and verified.", model.Name);
        }
        catch (OperationCanceledException)
        {
            SetState(model.Name, FaceModelState.Missing, null);
            throw;
        }
        catch (Exception ex)
        {
            // A failed fetch is not fatal: People simply waits ("Getting the face tools") and the next start or hourly retry tries again.
            logger.LogWarning(ex, "The face model {File} could not be downloaded.", model.Name);
            SetState(model.Name, FaceModelState.Problem, ex is InvalidDataException ? "The file did not match its checksum." : "The download did not finish.");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void SetState(string name, FaceModelState state, string? reason)
    {
        lock (_gate)
        {
            _states[name] = (state, reason);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "A half-downloaded face model file could not be removed.");
        }
    }
}

/// <summary>Fetches the face models once at start-up (and retries hourly while one is missing), so People is ready without the person doing anything.</summary>
public sealed class FaceModelDownloadService(FaceModelProvider store, ILogger<FaceModelDownloadService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.EnsureAsync(stoppingToken).ConfigureAwait(false);
                if (store.AreReady)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Getting the face tools failed; trying again later.");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
