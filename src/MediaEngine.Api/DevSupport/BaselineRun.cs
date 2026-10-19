using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.DevSupport;

/// <summary>
/// One complete, isolated baseline run: <c>--run-baseline --repo &lt;folder&gt; (--generate | --sample-root &lt;folder&gt;)
/// [--label &lt;name&gt;] [--config-source &lt;folder&gt;] [--out-root &lt;folder&gt;] [--budget-minutes N] [--poll-seconds N]</c>.
/// <c>tools/Run-IngestionBaseline.ps1</c> builds and then calls this. The run lives in
/// <c>&lt;out-root&gt;/&lt;run-id&gt;/</c>: a copy of the config folder, its own data store, logs and (with
/// <c>--generate</c>) samples. The Engine it starts is the same build as this process, runs from that copy on its own
/// port, and is the only process the run ever stops. The caller's config folder is only read; a sample root is only read.
/// </summary>
public static partial class BaselineRun
{
    public const int ExitFailed = 1;
    public const int ExitSampleRootChanged = 4;

    private const int EngineLeaseExitCode = 73;
    private const int EngineLeaseAttempts = 20;

    public static async Task<int> RunAsync(string[] arguments)
    {
        var repo = Path.GetFullPath(Option(arguments, "--repo") ?? throw new ArgumentException("--repo <folder> is required."));
        var generate = arguments.Contains("--generate");
        var sampleOption = Option(arguments, "--sample-root");
        if (generate == (sampleOption is not null))
        {
            throw new ArgumentException("Give exactly one of --generate or --sample-root <folder>.");
        }

        var label = Option(arguments, "--label") ?? string.Empty;
        if (!LabelPattern().IsMatch(label))
        {
            throw new ArgumentException("Label may only use letters, digits, dot, dash and underscore.");
        }

        var configSource = Path.GetFullPath(Option(arguments, "--config-source") ?? Path.Combine(repo, "config"));
        var outRoot = Path.GetFullPath(Option(arguments, "--out-root") ?? Path.Combine(repo, ".tmp", "ingestion-baseline"));
        var budget = TimeSpan.FromMinutes(double.Parse(Option(arguments, "--budget-minutes") ?? "180", CultureInfo.InvariantCulture));
        var poll = Option(arguments, "--poll-seconds") ?? "15";

        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (label.Length > 0 ? "-" + label : string.Empty);
        var run = Path.Combine(outRoot, runId);
        var configDirectory = Path.Combine(run, "config");
        var dataDirectory = Path.Combine(run, "data");
        var logDirectory = Path.Combine(run, "logs");
        var recordPath = Path.Combine(run, "baseline-record.json");
        Directory.CreateDirectory(logDirectory);
        Console.WriteLine($"Run folder: {run}");

        var exitCode = 0;
        Process? engine = null;
        try
        {
            var copiedKeys = CopyConfig(configSource, configDirectory);
            Console.WriteLine($"Provider key files copied into the run (names only): {(copiedKeys.Count > 0 ? string.Join(", ", copiedKeys) : "none")}");

            string sampleRoot;
            if (generate)
            {
                sampleRoot = Path.Combine(run, "samples");
                await BaselineSampleGenerator.RunAsync(sampleRoot).ConfigureAwait(false);
            }
            else
            {
                sampleRoot = Path.GetFullPath(sampleOption!);
                if (!Directory.Exists(sampleRoot))
                {
                    throw new DirectoryNotFoundException($"Sample root not found: {sampleRoot}");
                }
            }

            var inventoryBefore = Inventory(sampleRoot);
            Console.WriteLine($"Sample root: {sampleRoot} ({inventoryBefore.Count} entries)");
            await BaselinePrepare.RunAsync(configDirectory, sampleRoot).ConfigureAwait(false);

            var startedAt = DateTimeOffset.UtcNow;
            var engineLog = Path.Combine(run, "engine-console.log");
            int port;
            (engine, port) = await StartEngineAsync(run, configDirectory, dataDirectory, logDirectory, engineLog).ConfigureAwait(false);
            Console.WriteLine($"Engine started: PID {engine.Id} on port {port}");

            var waitExit = await BaselineWait.RunAsync(configDirectory,
                ["--budget-minutes", budget.TotalMinutes.ToString(CultureInfo.InvariantCulture), "--poll-seconds", poll, "--pid", engine.Id.ToString(CultureInfo.InvariantCulture)])
                .ConfigureAwait(false);
            var endedAt = DateTimeOffset.UtcNow;

            StopEngine(engine);
            Console.WriteLine($"Engine stopped (PID {engine.Id}).");

            RecordRunFacts(repo, configDirectory, label, startedAt, endedAt, waitExit == BaselineWait.ExitSettled, engineLog);
            await BaselineExport.RunAsync(configDirectory, recordPath).ConfigureAwait(false);
            if (label.Length > 0)
            {
                File.Copy(recordPath, Path.Combine(outRoot, label + ".json"), overwrite: true);
            }

            var inventoryAfter = Inventory(sampleRoot);
            if (inventoryBefore.Digest != inventoryAfter.Digest)
            {
                Console.WriteLine($"WARNING: the sample root changed during the run ({inventoryBefore.Count} -> {inventoryAfter.Count} entries). Investigate before trusting this record.");
                exitCode = ExitSampleRootChanged;
            }
            else
            {
                Console.WriteLine("Sample root unchanged (names, sizes and times match before and after).");
            }

            if (waitExit == BaselineWait.ExitTimedOut)
            {
                Console.WriteLine($"WARNING: ingestion did not settle within {budget.TotalMinutes:0} minutes; the record shows the state at that point.");
                exitCode = BaselineWait.ExitTimedOut;
            }
            else if (waitExit == BaselineWait.ExitEngineStopped)
            {
                Console.WriteLine("WARNING: the Engine stopped before ingestion settled.");
                exitCode = BaselineWait.ExitEngineStopped;
            }

            Console.WriteLine($"Baseline record: {recordPath}");
            Console.WriteLine($"Run took {endedAt - startedAt:hh\\:mm\\:ss}.");
        }
        finally
        {
            // Only the child this run started, and the copied keys always go.
            if (engine is { HasExited: false })
            {
                StopEngine(engine);
            }

            RemoveCopiedKeys(configDirectory);
        }

        return exitCode;
    }

    // -- config copy ----------------------------------------------------------------------------------------

    /// <summary>Copies the config folder (without backups, keys and *.bak) and the provider key files; returns the key file names.</summary>
    private static List<string> CopyConfig(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Config folder not found: {source}");
        }

        CopyDirectory(source, target, name => name is "backups" or ".keys" or "secrets" or ".secrets" || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        var secretsTarget = Directory.CreateDirectory(Path.Combine(target, "secrets")).FullName;
        var mainConfig = FindMainCheckoutConfig(source);
        var secretsSource = Path.Combine(source, "secrets");
        if (!Directory.Exists(secretsSource) || Directory.GetFiles(secretsSource, "*.json").Length == 0)
        {
            secretsSource = mainConfig is null ? secretsSource : Path.Combine(mainConfig, "secrets");
        }

        var copied = new List<string>();
        if (Directory.Exists(secretsSource))
        {
            foreach (var file in Directory.GetFiles(secretsSource, "*.json").Order(StringComparer.Ordinal))
            {
                File.Copy(file, Path.Combine(secretsTarget, Path.GetFileName(file)), overwrite: true);
                copied.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        var dotSecrets = Path.Combine(source, ".secrets");
        if (!Directory.Exists(dotSecrets) && mainConfig is not null)
        {
            dotSecrets = Path.Combine(mainConfig, ".secrets");
        }

        if (Directory.Exists(dotSecrets))
        {
            CopyDirectory(dotSecrets, Path.Combine(target, ".secrets"), _ => false);
        }

        return copied;
    }

    private static void CopyDirectory(string source, string target, Func<string, bool> skip)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source))
        {
            if (!skip(Path.GetFileName(directory)))
            {
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), skip);
            }
        }

        foreach (var file in Directory.GetFiles(source))
        {
            if (!skip(Path.GetFileName(file)))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }
    }

    /// <summary>
    /// A linked worktree keeps the provider keys (gitignored) in the main checkout: its <c>.git</c> is a file naming
    /// <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>. Returns that checkout's config folder, or null when this is the main checkout.
    /// </summary>
    private static string? FindMainCheckoutConfig(string configSource)
    {
        var repo = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(configSource));
        var dotGit = repo is null ? null : Path.Combine(repo, ".git");
        if (dotGit is null || !File.Exists(dotGit))
        {
            return null;
        }

        var line = File.ReadAllLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        var gitDirectory = line?["gitdir:".Length..].Trim();
        var main = gitDirectory is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(gitDirectory))));
        return main is null ? null : Path.Combine(main, "config");
    }

    private static void RemoveCopiedKeys(string configDirectory)
    {
        foreach (var name in new[] { "secrets", ".secrets" })
        {
            var path = Path.Combine(configDirectory, name);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    // -- the Engine -----------------------------------------------------------------------------------------

    private static async Task<(Process Engine, int Port)> StartEngineAsync(
        string run, string configDirectory, string dataDirectory, string logDirectory, string engineLog)
    {
        var enginePath = typeof(BaselineRun).Assembly.Location;
        var port = FreePort();
        for (var attempt = 1; ; attempt++)
        {
            var info = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = run,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(enginePath);
            foreach (var name in new[] { "TUVIMA_DB_PATH", "TUVIMA_LIBRARY_ROOT" })
            {
                info.Environment.Remove(name);
            }

            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            info.Environment["DOTNET_ENVIRONMENT"] = "Development";
            info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            info.Environment["ASPNETCORE_CONTENTROOT"] = Path.GetDirectoryName(enginePath)!;
            info.Environment["TUVIMA_CONFIG_DIR"] = configDirectory;
            info.Environment["TUVIMA_DATA_DIR"] = dataDirectory;
            info.Environment["TUVIMA_LOG_DIR"] = logDirectory;
            info.Environment["TUVIMA_BACKUP_DIR"] = Path.Combine(run, "backups");
            info.Environment["TUVIMA_DATA_PROTECTION_DIR"] = Path.Combine(run, "keys");
            info.Environment["TUVIMA_MODELS_DIR"] = Path.Combine(run, "models");
            info.Environment["TUVIMA_AI_RUNTIME_DIR"] = Path.Combine(run, "ai-runtime");

            var engine = new Process { StartInfo = info };
            var writer = new StreamWriter(engineLog, append: true, new UTF8Encoding(false)) { AutoFlush = true };
            var gate = new object();
            DataReceivedEventHandler append = (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (gate)
                    {
                        writer.WriteLine(e.Data);
                    }
                }
            };
            engine.OutputDataReceived += append;
            engine.ErrorDataReceived += append;
            engine.Start();
            engine.BeginOutputReadLine();
            engine.BeginErrorReadLine();

            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (!engine.HasExited)
            {
                return (engine, port);
            }

            // Exit code 73: another Engine (or a test host) holds the Engine process lease. Wait for it rather than fight it.
            if (engine.ExitCode != EngineLeaseExitCode || attempt >= EngineLeaseAttempts)
            {
                throw new InvalidOperationException($"The Engine exited early with code {engine.ExitCode}. See {engineLog}");
            }

            Console.WriteLine($"Another Tuvima Engine holds the process lease; retrying in 30 s (attempt {attempt} of {EngineLeaseAttempts}).");
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    }

    private static void StopEngine(Process engine)
    {
        if (engine.HasExited)
        {
            return;
        }

        // Kills exactly the process this run started, and anything it started in turn.
        engine.Kill(entireProcessTree: true);
        engine.WaitForExit(30_000);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // -- facts and inventory --------------------------------------------------------------------------------

    private static void RecordRunFacts(
        string repo, string configDirectory, string label, DateTimeOffset startedAt, DateTimeOffset endedAt, bool settled, string engineLog)
    {
        var hosts = new JsonObject();
        if (File.Exists(engineLog))
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            using var log = new StreamReader(new FileStream(engineLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (log.ReadLine() is { } line)
            {
                if (RequestPattern().Match(line) is { Success: true } match)
                {
                    var host = match.Groups[1].Value;
                    counts[host] = counts.GetValueOrDefault(host) + 1;
                }
            }

            foreach (var (host, count) in counts)
            {
                hosts[host] = count;
            }
        }

        var path = BaselinePrepare.RunInfoPathFor(configDirectory);
        var info = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        info["label"] = label;
        info["build_commit"] = BuildCommit(repo);
        info["started_at"] = startedAt.ToString("O", CultureInfo.InvariantCulture);
        info["ended_at"] = endedAt.ToString("O", CultureInfo.InvariantCulture);
        info["settled"] = settled;
        info["http_hosts"] = hosts;
        File.WriteAllText(path, info.ToJsonString(RealMediaHarness.Json));
    }

    /// <summary>The short commit, with "+uncommitted" when source, tools or provider files differ from it.</summary>
    private static string BuildCommit(string repo)
    {
        var commit = Git(repo, "rev-parse", "--short=12", "HEAD")?.Trim();
        if (string.IsNullOrEmpty(commit))
        {
            return "unknown";
        }

        var dirty = Git(repo, "status", "--porcelain", "--", "src", "tools", "config/providers");
        return string.IsNullOrWhiteSpace(dirty) ? commit : commit + "+uncommitted";
    }

    private static string? Git(string repo, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(repo);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var git = Process.Start(info);
            if (git is null)
            {
                return null;
            }

            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return git.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Git is not installed on this machine: the record simply names no build commit.
            return null;
        }
    }

    /// <summary>Name, size and last-write time of everything under the root: enough to prove nothing was touched.</summary>
    public static (int Count, string Digest) Inventory(string root)
    {
        var lines = new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .Select(entry => string.Create(CultureInfo.InvariantCulture,
                $"{entry.FullName}|{(entry is FileInfo file ? file.Length.ToString(CultureInfo.InvariantCulture) : "dir")}|{entry.LastWriteTimeUtc.Ticks}"))
            .ToList();
        return (lines.Count, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))));
    }

    private static string? Option(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]*$")]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"Sending HTTP request \w+ https?://([^/\s?]+)")]
    private static partial Regex RequestPattern();
}
