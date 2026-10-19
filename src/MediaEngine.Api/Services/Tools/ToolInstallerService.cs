using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// Fetches FFmpeg and Tesseract when a person asks. Every download is a fixed https address on github.com from
/// <c>config/tool_downloads.json</c> and must match its recorded SHA-256 before anything is placed; a file that does
/// not match is thrown away and the old copy is left alone. Nothing downloads by itself, no library data is sent, and
/// every attempt is written to the system activity log in plain words.
/// </summary>
public sealed class ToolInstallerService : IToolInstallerService
{
    private const long MaxArchiveBytes = 1L << 30;
    private const long MaxInstallerBytes = 300L << 20;
    private const long MaxLanguageFileBytes = 100L << 20;
    private const long MaxExtractedFileBytes = 600L << 20;
    private const long MaxLicenseBytes = 1L << 20;
    private const string LicenseFileName = "LICENSE.txt";

    private readonly IConfigurationLoader _configuration;
    private readonly TuvimaDataPaths _paths;
    private readonly IOcrService _ocr;
    private readonly IToolDownloadSource _source;
    private readonly IToolProcessLauncher _launcher;
    private readonly ISystemActivityRepository _activity;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ToolInstallPlatform _platform;
    // The tools list needs this service for its rows and this service needs the tools list to look again, so the
    // list is reached through a delegate instead of a constructor cycle.
    private readonly Func<IToolRequirementsService> _tools;
    private readonly ILogger<ToolInstallerService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    public ToolInstallerService(
        IConfigurationLoader configuration,
        TuvimaDataPaths paths,
        IOcrService ocr,
        IToolDownloadSource source,
        IToolProcessLauncher launcher,
        ISystemActivityRepository activity,
        IHostApplicationLifetime lifetime,
        ToolInstallPlatform platform,
        Func<IToolRequirementsService> tools,
        ILogger<ToolInstallerService> logger)
    {
        _configuration = configuration;
        _paths = paths;
        _ocr = ocr;
        _source = source;
        _launcher = launcher;
        _activity = activity;
        _lifetime = lifetime;
        _platform = platform;
        _tools = tools;
        _logger = logger;
    }

    private string ToolsRoot => Path.Combine(_paths.Root, "tools");

    // ── Who can install ──────────────────────────────────────────────────────

    public ToolInstallOffer GetOffer(string toolId, string installKind)
    {
        // Pinned builds exist for 64-bit Windows only, and a container never downloads into itself.
        if (!_platform.WindowsX64 || installKind == ToolInstallKinds.Docker)
        {
            return ToolInstallOffer.None;
        }

        var downloads = _configuration.LoadToolDownloads();
        if (toolId == ToolRequirementIds.Ffmpeg)
        {
            return new ToolInstallOffer(downloads.Ffmpeg is not null);
        }

        if (toolId == ToolRequirementIds.Tesseract)
        {
            // A repair of a Windows install is a reinstall of the Library; only a run from source fetches Tesseract.
            if (downloads.Tesseract is null || installKind != ToolInstallKinds.Source)
            {
                return ToolInstallOffer.None;
            }

            // Tesseract's setup shows a window and asks for approval, which a Windows service cannot do.
            return _platform.Interactive
                ? new ToolInstallOffer(true)
                : new ToolInstallOffer(false, ToolRequirementsService.Copy.ToolFix(
                    ToolInstallKinds.WindowsInstaller, "Tesseract", ToolRequirementsService.Copy.TesseractPackages));
        }

        return ToolInstallOffer.None;
    }

    public ToolInstallSnapshot GetSnapshot(string toolId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(toolId, out var job))
            {
                return ToolInstallSnapshot.Idle;
            }

            // A setup program that was deleted or already used is no longer waiting for anyone.
            if (job.Phase == ToolInstallPhase.InstallerReady && !job.InstallerRunning
                && (job.InstallerPath is null || !File.Exists(job.InstallerPath)))
            {
                job.Phase = ToolInstallPhase.Idle;
                job.InstallerPath = null;
                job.Reason = null;
            }

            return new ToolInstallSnapshot(job.Phase, job.Percent, job.Reason, job.InstallerRunning);
        }
    }

    // ── Start and cancel ─────────────────────────────────────────────────────

    public async Task<ToolInstallResult> StartAsync(string toolId, CancellationToken ct)
    {
        var catalogue = await _tools().GetAsync(ct).ConfigureAwait(false);
        var row = catalogue.Tools.FirstOrDefault(tool => tool.Id == toolId);
        if (row is null)
        {
            return new ToolInstallResult(ToolInstallOutcome.UnknownTool);
        }

        lock (_gate)
        {
            if (_jobs.TryGetValue(toolId, out var running) && running.IsRunning)
            {
                return new ToolInstallResult(ToolInstallOutcome.AlreadyRunning);
            }

            if (!(row.CanInstall || row.CanDownloadAgain))
            {
                return new ToolInstallResult(ToolInstallOutcome.NotOffered, ToolRequirementsService.Copy.InstallNotOffered);
            }

            var job = new Job(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping));
            _jobs[toolId] = job;
            job.Execution = Task.Run(() => RunJobAsync(toolId, job));
        }

        return new ToolInstallResult(ToolInstallOutcome.Accepted);
    }

    public async Task CancelAsync(string toolId, CancellationToken ct)
    {
        Task execution;
        lock (_gate)
        {
            // Once the new copy is in place there is nothing left to cancel; the job only finishes and looks again.
            if (!_jobs.TryGetValue(toolId, out var job) || job.Phase != ToolInstallPhase.Downloading || job.Committed)
            {
                return;
            }

            // A job only leaves Downloading under this lock, and only afterwards releases its token source.
            job.Cts.Cancel();
            execution = job.Execution;
        }

        await execution.WaitAsync(ct).ConfigureAwait(false);
    }

    // ── The job ──────────────────────────────────────────────────────────────

    private async Task RunJobAsync(string toolId, Job job)
    {
        var ct = job.Cts.Token;
        var isFfmpeg = toolId == ToolRequirementIds.Ffmpeg;
        var downloads = _configuration.LoadToolDownloads();
        var name = isFfmpeg ? "FFmpeg" : "Tesseract";
        var version = isFfmpeg ? downloads.Ffmpeg?.Version : downloads.Tesseract?.Version;
        var host = ToolDownloadSettings.AllowedHost;
        var work = Path.Combine(ToolsRoot, ".downloads", toolId);
        try
        {
            ResetFolder(work);
            await LogAsync(ToolDownloadActivity.Started(name, version, host)).ConfigureAwait(false);

            if (isFfmpeg)
            {
                await InstallFfmpegAsync(job, downloads.Ffmpeg ?? throw NotConfigured(), work, ct).ConfigureAwait(false);
                await LogAsync(ToolDownloadActivity.Finished(name, version, host)).ConfigureAwait(false);
                await CheckQuietlyAsync().ConfigureAwait(false);
                Finish(job, ToolInstallPhase.Idle, null);
            }
            else
            {
                await DownloadTesseractAsync(job, downloads.Tesseract ?? throw NotConfigured(), work, ct).ConfigureAwait(false);
                await LogAsync(ToolDownloadActivity.Finished(name, version, host)).ConfigureAwait(false);
                Finish(job, ToolInstallPhase.InstallerReady, null);
            }
        }
        // Every ending records its activity entry before the job leaves Downloading, as the success path does: anyone who
        // sees the job finished (the Tools page, a test waiting on it) can then rely on the history already saying how.
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !job.Committed)
        {
            await LogAsync(ToolDownloadActivity.Cancelled(name, version, host)).ConfigureAwait(false);
            Finish(job, ToolInstallPhase.Idle, null);
        }
        catch (ToolChecksumException ex)
        {
            _logger.LogWarning("A download for {Tool} was refused: {Detail}", name, ex.Message);
            await LogAsync(ToolDownloadActivity.ChecksumRefused(name, version, host)).ConfigureAwait(false);
            Finish(job, ToolInstallPhase.Failed, ToolRequirementsService.Copy.InstallChecksum);
        }
        catch (ToolInstallException ex)
        {
            _logger.LogWarning(ex, "Installing {Tool} did not finish.", name);
            await LogAsync(ToolDownloadActivity.Failed(name, version, host, ex.PlainReason)).ConfigureAwait(false);
            Finish(job, ToolInstallPhase.Failed, ex.PlainReason);
        }
        catch (Exception ex)
        {
            // A background job must never take the Engine down; the person gets a plain reason and the log gets the detail.
            _logger.LogWarning(ex, "Installing {Tool} failed.", name);
            await LogAsync(ToolDownloadActivity.Failed(name, version, host, ToolRequirementsService.Copy.InstallFailed)).ConfigureAwait(false);
            Finish(job, ToolInstallPhase.Failed, ToolRequirementsService.Copy.InstallFailed);
        }
        finally
        {
            DeleteQuietly(work);
            job.Cts.Dispose();
        }
    }

    private static ToolInstallException NotConfigured() =>
        new(ToolRequirementsService.Copy.InstallNotOffered, "No download is configured for this tool.");

    // ── FFmpeg ───────────────────────────────────────────────────────────────

    private async Task InstallFfmpegAsync(Job job, FfmpegDownload entry, string work, CancellationToken ct)
    {
        var archive = Path.Combine(work, "ffmpeg.zip");
        await DownloadVerifiedAsync(entry.Url, archive, entry.Sha256, MaxArchiveBytes,
            fraction => SetPercent(job, fraction * 85), ct).ConfigureAwait(false);

        SetPercent(job, 88);
        var staging = Path.Combine(work, "staged");
        Directory.CreateDirectory(staging);
        ExtractWanted(archive, staging, entry, ct);

        SetPercent(job, 94);
        foreach (var file in entry.Files)
        {
            var actual = await HashFileAsync(Path.Combine(staging, file.Name), ct).ConfigureAwait(false);
            if (!HashesMatch(actual, file.Sha256))
            {
                throw new ToolChecksumException($"{file.Name} inside the archive did not match its recorded checksum.");
            }
        }

        ct.ThrowIfCancellationRequested();
        ReplaceFolder(staging, Path.Combine(ToolsRoot, "ffmpeg"));
        lock (_gate)
        {
            job.Committed = true;
        }

        SetPercent(job, 98);

        // The person asked the Library to fetch its own copy, so a saved location that no longer works must not
        // keep winning over it (an explicit path is authoritative during detection).
        var settings = _configuration.LoadTranscoding();
        if (!string.IsNullOrWhiteSpace(settings.FfmpegBinaryPath) || !string.IsNullOrWhiteSpace(settings.FfprobeBinaryPath))
        {
            settings.FfmpegBinaryPath = string.Empty;
            settings.FfprobeBinaryPath = string.Empty;
            _configuration.SaveTranscoding(settings);
        }
    }

    /// <summary>
    /// Takes out only the named programs and the licence, by file name. The entry's own path is never used to place
    /// anything, and an entry whose path climbs out of the archive or names another drive is ignored.
    /// </summary>
    private static void ExtractWanted(string archive, string staging, FfmpegDownload entry, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        var wanted = entry.Files.ToDictionary(file => file.Name, file => file, StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var licenses = new SortedDictionary<int, ZipArchiveEntry>();

        foreach (var item in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryLeafName(item.FullName, out var leaf))
            {
                continue;
            }

            if (wanted.TryGetValue(leaf, out var file) && taken.Add(file.Name))
            {
                CopyEntry(item, Path.Combine(staging, file.Name), MaxExtractedFileBytes);
                continue;
            }

            var rank = entry.LicenseNames.FindIndex(name => string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase));
            if (rank >= 0)
            {
                licenses.TryAdd(rank, item);
            }
        }

        if (taken.Count != wanted.Count)
        {
            throw new ToolInstallException(ToolRequirementsService.Copy.InstallArchiveUnexpected,
                "The archive did not contain every program that was expected.");
        }

        if (licenses.Count > 0)
        {
            CopyEntry(licenses.First().Value, Path.Combine(staging, LicenseFileName), MaxLicenseBytes);
        }
    }

    private static bool TryLeafName(string fullName, out string leaf)
    {
        leaf = string.Empty;
        var normalized = fullName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment == ".."))
        {
            return false;
        }

        leaf = segments[^1];
        return leaf.Length > 0;
    }

    private static void CopyEntry(ZipArchiveEntry entry, string destination, long maxBytes)
    {
        if (entry.Length > maxBytes)
        {
            throw new ToolInstallException(ToolRequirementsService.Copy.InstallArchiveUnexpected,
                $"{entry.Name} is larger than expected.");
        }

        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new ToolInstallException(ToolRequirementsService.Copy.InstallArchiveUnexpected,
                    $"{entry.Name} is larger than expected.");
            }

            output.Write(buffer, 0, read);
        }
    }

    /// <summary>Puts the new folder in place of the old one; the old copy is kept if anything goes wrong.</summary>
    private void ReplaceFolder(string staged, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Outside the job's own work folder, which is always deleted: if putting the old copy back fails it must stay.
        var previous = Path.Combine(ToolsRoot, ".previous", Path.GetFileName(target) + "-" + Guid.NewGuid().ToString("N"));
        var movedAside = false;
        try
        {
            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                Directory.Move(target, previous);
                movedAside = true;
            }

            Directory.Move(staged, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not put the new copy of a tool in place; the old copy was kept.");
            if (movedAside && !Directory.Exists(target))
            {
                try
                {
                    Directory.Move(previous, target);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    // Never delete the only remaining copy: it stays in the previous-copies folder for the person to recover.
                    _logger.LogError(restoreError, "Could not put the previous copy of a tool back; it was left at {Previous}.", previous);
                }
            }

            throw new ToolInstallException(ToolRequirementsService.Copy.InstallFolderBusy, "The tool folder could not be replaced.", ex);
        }

        if (movedAside)
        {
            DeleteQuietly(previous);
        }
    }

    // ── Tesseract ────────────────────────────────────────────────────────────

    private async Task DownloadTesseractAsync(Job job, TesseractDownload entry, string work, CancellationToken ct)
    {
        // Language data first (small), straight into the folder text recognition reads, skipping what is already right.
        var dataFolder = _ocr.LanguageDataDirectory;
        Directory.CreateDirectory(dataFolder);
        var count = Math.Max(1, entry.LanguageFiles.Count);
        for (var index = 0; index < entry.LanguageFiles.Count; index++)
        {
            var file = entry.LanguageFiles[index];
            var target = Path.Combine(dataFolder, file.Name);
            if (File.Exists(target) && HashesMatch(await HashFileAsync(target, ct).ConfigureAwait(false), file.Sha256))
            {
                SetPercent(job, 20.0 * (index + 1) / count);
                continue;
            }

            var temp = Path.Combine(work, file.Name);
            var first = index;
            await DownloadVerifiedAsync(file.Url, temp, file.Sha256, MaxLanguageFileBytes,
                fraction => SetPercent(job, 20.0 * (first + fraction) / count), ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }

        _ocr.Invalidate();

        var installerTemp = Path.Combine(work, entry.Installer.Name);
        await DownloadVerifiedAsync(entry.Installer.Url, installerTemp, entry.Installer.Sha256, MaxInstallerBytes,
            fraction => SetPercent(job, 20 + fraction * 80), ct).ConfigureAwait(false);

        var installers = Path.Combine(ToolsRoot, "installers");
        Directory.CreateDirectory(installers);
        var installerPath = Path.Combine(installers, entry.Installer.Name);
        File.Move(installerTemp, installerPath, overwrite: true);
        lock (_gate)
        {
            job.InstallerPath = installerPath;
        }
    }

    public async Task<ToolInstallResult> RunInstallerAsync(string toolId, CancellationToken ct)
    {
        if (toolId != ToolRequirementIds.Tesseract)
        {
            return new ToolInstallResult(ToolInstallOutcome.NotOffered, ToolRequirementsService.Copy.NoInstallerReady);
        }

        if (!_platform.WindowsX64 || !_platform.Interactive)
        {
            return new ToolInstallResult(ToolInstallOutcome.NotOffered, ToolRequirementsService.Copy.ToolFix(
                ToolInstallKinds.WindowsInstaller, "Tesseract", ToolRequirementsService.Copy.TesseractPackages));
        }

        string path;
        Job current;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(toolId, out var job) || job.Phase != ToolInstallPhase.InstallerReady || job.InstallerPath is null)
            {
                return new ToolInstallResult(ToolInstallOutcome.NotOffered, ToolRequirementsService.Copy.NoInstallerReady);
            }

            if (job.InstallerRunning)
            {
                return new ToolInstallResult(ToolInstallOutcome.AlreadyRunning);
            }

            job.InstallerRunning = true;
            path = job.InstallerPath;
            current = job;
        }

        if (!File.Exists(path))
        {
            ReleaseInstaller(current, ToolInstallPhase.Idle, null, clearPath: true);
            return new ToolInstallResult(ToolInstallOutcome.NotOffered, ToolRequirementsService.Copy.NoInstallerReady);
        }

        // The checked file is held open (readable by others, not changeable) from the hash until the program has
        // started, so what was checked is exactly what runs. The service's own lifetime token is used, never the
        // request's, so a closed browser tab cannot leave the row stuck on "running".
        FileStream? held = null;
        try
        {
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var expected = _configuration.LoadToolDownloads().Tesseract?.Installer.Sha256;
            if (expected is null || !HashesMatch(await HashStreamAsync(held, _lifetime.ApplicationStopping).ConfigureAwait(false), expected))
            {
                await held.DisposeAsync().ConfigureAwait(false);
                held = null;
                DeleteQuietly(path);
                ReleaseInstaller(current, ToolInstallPhase.Failed, ToolRequirementsService.Copy.InstallerChecksum, clearPath: true);
                return new ToolInstallResult(ToolInstallOutcome.NotOffered, ToolRequirementsService.Copy.InstallerChecksum);
            }

            var stream = held;
            held = null;
            lock (_gate)
            {
                current.Execution = Task.Run(() => RunInstallerCoreAsync(current, path, stream));
            }

            return new ToolInstallResult(ToolInstallOutcome.Accepted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A locked file or an Engine that is stopping: the installer is still there, so it can be run again.
            _logger.LogWarning(ex, "Tesseract's installer could not be checked before running.");
            ReleaseInstaller(current, ToolInstallPhase.InstallerReady, ToolRequirementsService.Copy.InstallerCouldNotBeChecked, clearPath: false);
            return new ToolInstallResult(ToolInstallOutcome.NotOffered, ToolRequirementsService.Copy.InstallerCouldNotBeChecked);
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Marks the setup program as not running and moves the job to the given phase.</summary>
    private void ReleaseInstaller(Job job, ToolInstallPhase phase, string? reason, bool clearPath)
    {
        lock (_gate)
        {
            job.InstallerRunning = false;
            job.Phase = phase;
            job.Reason = reason;
            if (clearPath)
            {
                job.InstallerPath = null;
            }
        }
    }

    private async Task RunInstallerCoreAsync(Job job, string path, FileStream held)
    {
        var stopping = _lifetime.ApplicationStopping;
        string? note = null;
        var exitCode = -1;
        var ready = false;
        try
        {
            try
            {
                // The held handle is let go the moment the program has started, and not before.
                exitCode = await _launcher.RunAndWaitAsync(path, held.Dispose, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                note = ToolRequirementsService.Copy.TesseractInstallerReady;
            }
            catch (Exception ex)
            {
                // Most often the person said no to Windows' approval prompt; whatever it was, the row must be usable again.
                _logger.LogInformation(ex, "Tesseract's installer did not start.");
                note = ToolRequirementsService.Copy.InstallerDidNotStart;
            }
            finally
            {
                await held.DisposeAsync().ConfigureAwait(false);
            }

            if (note is null)
            {
                ready = await ReadyAfterInstallerAsync(exitCode).ConfigureAwait(false);
            }

            if (ready)
            {
                // The setup program has done its job; it goes before the row stops showing it as waiting.
                DeleteQuietly(path);
            }
        }
        finally
        {
            lock (_gate)
            {
                job.InstallerRunning = false;
                if (ready)
                {
                    job.Phase = ToolInstallPhase.Idle;
                    job.InstallerPath = null;
                    job.Reason = null;
                }
                else
                {
                    job.Reason = note ?? ToolRequirementsService.Copy.TesseractNotReadyYet;
                }
            }
        }
    }

    private async Task<bool> ReadyAfterInstallerAsync(int exitCode)
    {
        try
        {
            if (exitCode == 0)
            {
                // A saved location that does not hold a working Tesseract (gone, or found but broken) must not win
                // over the copy just installed, so it is cleared, as it is after an FFmpeg install.
                var settings = _configuration.LoadOcr();
                if (!string.IsNullOrWhiteSpace(settings.TesseractBinaryPath))
                {
                    _ocr.Invalidate();
                    var saved = await _ocr.GetCapabilityAsync(_lifetime.ApplicationStopping).ConfigureAwait(false);
                    if (!saved.IsAvailable)
                    {
                        settings.TesseractBinaryPath = string.Empty;
                        _configuration.SaveOcr(settings);
                    }
                }
            }

            _ocr.Invalidate();
            var tools = await _tools().CheckAsync(_lifetime.ApplicationStopping).ConfigureAwait(false);
            return tools.Tools.Any(tool => tool.Id == ToolRequirementIds.Tesseract && tool.State == ToolRequirementStates.Ready);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Looking for Tesseract after its installer closed failed.");
            return false;
        }
    }

    // ── Downloading ──────────────────────────────────────────────────────────

    /// <summary>Downloads to <paramref name="destination"/>, hashing as it goes, and keeps the file only if it matches.</summary>
    private async Task DownloadVerifiedAsync(
        string address, string destination, string expectedSha256, long maxBytes, Action<double> onFraction, CancellationToken ct)
    {
        if (!ToolDownloadSettings.IsAllowedAddress(address))
        {
            throw new ToolInstallException(ToolRequirementsService.Copy.InstallAddressRefused, "A download address was refused.");
        }

        ToolDownload download;
        try
        {
            download = await _source.OpenAsync(new Uri(address), ct).ConfigureAwait(false);
        }
        catch (ToolDownloadRefusedException ex)
        {
            throw new ToolInstallException(ToolRequirementsService.Copy.InstallAddressRefused, ex.Message, ex);
        }

        await using var _ = download.ConfigureAwait(false);
        if (download.Length is { } declared)
        {
            if (declared > maxBytes)
            {
                throw new ToolInstallException(ToolRequirementsService.Copy.InstallArchiveUnexpected, "The download is larger than expected.");
            }

            EnsureFreeSpace(Path.GetDirectoryName(destination)!, declared);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await download.Content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new ToolInstallException(ToolRequirementsService.Copy.InstallArchiveUnexpected, "The download is larger than expected.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (download.Length is > 0)
                {
                    onFraction(Math.Min(1.0, (double)total / download.Length.Value));
                }
            }
        }

        if (download.Length is { } length && total != length)
        {
            throw new ToolInstallException(ToolRequirementsService.Copy.InstallFailed, "The download ended early.");
        }

        if (!HashesMatch(Convert.ToHexString(hash.GetHashAndReset()), expectedSha256))
        {
            File.Delete(destination);
            throw new ToolChecksumException($"The download from {ToolDownloadSettings.AllowedHost} did not match its recorded checksum.");
        }

        onFraction(1.0);
    }

    private void EnsureFreeSpace(string folder, long downloadBytes)
    {
        long available;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Free space could not be read; the download itself will fail plainly if there really is none.
            _logger.LogDebug(ex, "Could not read free space before a tool download.");
            return;
        }

        // Room for the download, its unpacked contents and the copy it replaces.
        if (available < downloadBytes * 3)
        {
            throw new ToolInstallException(ToolRequirementsService.Copy.InstallNoSpace, "Not enough free space for the download.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await HashStreamAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task<string> HashStreamAsync(Stream stream, CancellationToken ct) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));

    private static bool HashesMatch(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    // ── Small helpers ────────────────────────────────────────────────────────

    private void SetPercent(Job job, double percent)
    {
        lock (_gate)
        {
            if (job.Phase == ToolInstallPhase.Downloading)
            {
                job.Percent = Math.Clamp((int)percent, 0, 100);
            }
        }
    }

    private void Finish(Job job, ToolInstallPhase phase, string? reason)
    {
        lock (_gate)
        {
            job.Phase = phase;
            job.Reason = reason;
        }
    }

    private async Task CheckQuietlyAsync()
    {
        try
        {
            // Looks for the tool again, which also lets files that were waiting for it continue.
            await _tools().CheckAsync(_lifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Looking for the tools after an install failed.");
        }
    }

    private async Task LogAsync(SystemActivityEntry entry)
    {
        try
        {
            await _activity.LogAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The log is a record of what happened, never a reason for an install to fail.
            _logger.LogDebug(ex, "Could not record the {Action} activity entry.", entry.ActionType);
        }
    }

    private static void ResetFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Directory.CreateDirectory(folder);
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove a temporary tool download file.");
        }
    }

    private sealed class Job(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public ToolInstallPhase Phase { get; set; } = ToolInstallPhase.Downloading;
        public int Percent { get; set; }
        public string? Reason { get; set; }
        public bool InstallerRunning { get; set; }

        /// <summary>The new copy is in place, so the job can no longer be cancelled.</summary>
        public bool Committed { get; set; }
        public string? InstallerPath { get; set; }
        public Task Execution { get; set; } = Task.CompletedTask;
        public bool IsRunning => Phase == ToolInstallPhase.Downloading || InstallerRunning;
    }

    /// <summary>A failure with a plain reason for the person; the detail is for the log only.</summary>
    private sealed class ToolInstallException(string plainReason, string detail, Exception? inner = null)
        : Exception(detail, inner)
    {
        public string PlainReason { get; } = plainReason;
    }

    /// <summary>A download (or a program inside it) did not match its recorded SHA-256.</summary>
    private sealed class ToolChecksumException(string detail) : Exception(detail);
}
