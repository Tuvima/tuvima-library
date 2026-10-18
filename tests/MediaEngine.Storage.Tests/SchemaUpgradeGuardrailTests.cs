using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Storage.Schema.Upgrades;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Guardrails for the numbered data store upgrade steps: the generated structure file is current, released steps
/// are never edited, and no other startup code patches the structure.
/// </summary>
public sealed class SchemaUpgradeGuardrailTests
{
    private const string SnapshotPath = "src/MediaEngine.Storage/Schema/schema.snapshot.sql";
    private const string UpgradesFolder = "src/MediaEngine.Storage/Schema/Upgrades";
    private const string ReleasedStepsPath = UpgradesFolder + "/released-steps.txt";
    private const string TopUpPath = "src/MediaEngine.Storage/Schema/ReferenceDataTopUp.cs";
    private const string AllowlistFile = "SchemaUpgradeGuardrailAllowlist.txt";
    private const string RefreshCommand = "pwsh -File tools/Update-SchemaSnapshot.ps1";

    /// <summary>
    /// The only tables the reference-data top-up may insert into (decision D12 of the data store upgrade-steps spec,
    /// engineering/specs/data-store-upgrade-scripts.md). Adding a table here is a spec change, not a guardrail tweak.
    /// </summary>
    private static readonly string[] TopUpAllowedTables =
    [
        "metadata_providers",
        "onboarding_workflows",
        "onboarding_steps",
        "profiles",
    ];

    private static readonly Regex[] StructurePatchPatterns =
    [
        new(@"\bALTER\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        // Plain CREATE TABLE / CREATE [UNIQUE] INDEX. CREATE TEMP TABLE and CREATE TEMP INDEX do not match: they
        // never change the stored structure.
        new(@"\bCREATE\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bDROP\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bpragma_table_x?info\b|\bPRAGMA\s+table_x?info\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
    ];

    [Fact]
    public void SchemaSnapshot_MatchesTheUpgradeSteps()
    {
        var root = FindRepoRoot();
        var snapshotPath = Path.Combine(root, SnapshotPath);
        var receivedPath = ReceivedPath(snapshotPath, ".sql");
        var actual = SchemaSnapshotGenerator.Generate();

        var expected = File.Exists(snapshotPath) ? Normalize(File.ReadAllText(snapshotPath)) : string.Empty;
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            DeleteIfExists(receivedPath);
            return;
        }

        File.WriteAllText(receivedPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.Fail(
            $"The data structure changed. Run {RefreshCommand} and commit schema.snapshot.sql.{Environment.NewLine}" +
            $"  committed: {snapshotPath}{Environment.NewLine}" +
            $"  received:  {receivedPath}");
    }

    [Fact]
    public void ReleasedUpgradeSteps_AreNeverEdited()
    {
        var root = FindRepoRoot();
        var listPath = Path.Combine(root, ReleasedStepsPath);
        var receivedPath = ReceivedPath(listPath, ".txt");
        var current = ReadCurrentStepHashes(root);
        var listed = File.Exists(listPath) ? ParseReleasedSteps(File.ReadAllLines(listPath)) : [];

        var problems = new List<string>();
        foreach (var step in current)
        {
            if (!listed.TryGetValue(step.Number, out var entry))
            {
                problems.Add($"Upgrade step {Format(step.Number)} ({step.Name}) is not in released-steps.txt. Run {RefreshCommand} to record it.");
            }
            else if (!string.Equals(entry.Name, step.Name, StringComparison.Ordinal)
                     || !string.Equals(entry.Hash, step.Hash, StringComparison.Ordinal))
            {
                problems.Add($"Upgrade step {Format(step.Number)} ({entry.Name}) changed after release. Never edit a released step - revert it and add a new step.");
            }
        }

        var currentNumbers = current.Select(step => step.Number).ToHashSet();
        foreach (var entry in listed.Values.Where(entry => !currentNumbers.Contains(entry.Number)))
        {
            problems.Add($"Released upgrade step {Format(entry.Number)} ({entry.Name}) no longer exists. Never remove a released step - restore it and add a new step.");
        }

        if (problems.Count == 0)
        {
            DeleteIfExists(receivedPath);
            return;
        }

        File.WriteAllText(receivedPath, RenderReleasedSteps(current), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.Fail(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void UpgradeStepNumbers_AreContiguousFromOne()
    {
        var steps = DataStoreUpgradeCatalog.ForProduct();

        Assert.NotEmpty(steps);
        Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(step => step.Number));
        // Validate throws, naming the offending step, on a gap, a duplicate or a badly formed name.
        DataStoreUpgradeCatalog.Validate(steps);
    }

    [Fact]
    public void StartupCode_DoesNotPatchTheStructureOutsideUpgradeSteps()
    {
        var root = FindRepoRoot();
        var allowlist = ReadAllowlist();
        var upgradesPrefix = UpgradesFolder + "/";
        var hits = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsBuildOutput(relative) || relative.StartsWith(upgradesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            if (!StructurePatchPatterns.Any(pattern => pattern.IsMatch(source)))
            {
                continue;
            }

            if (allowlist.ContainsKey(relative))
            {
                used.Add(relative);
                continue;
            }

            hits.Add(relative);
        }

        var stale = allowlist.Keys.Where(path => !used.Contains(path)).ToList();

        Assert.True(
            hits.Count == 0,
            "These files change or inspect the data structure outside the numbered upgrade steps (ALTER TABLE, CREATE TABLE, CREATE INDEX, DROP TABLE or table_info). "
            + "Put structure changes in a numbered upgrade step in src/MediaEngine.Storage/Schema/Upgrades/. "
            + $"A file with a real reason to match is listed, with the reason, in tests/MediaEngine.Storage.Tests/{AllowlistFile}:{Environment.NewLine}"
            + string.Join(Environment.NewLine, hits.Order(StringComparer.Ordinal)));
        Assert.True(
            stale.Count == 0,
            $"{AllowlistFile} lists files that no longer match the rule. Remove these entries:{Environment.NewLine}"
            + string.Join(Environment.NewLine, stale.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void ReferenceDataTopUp_OnlyAddsMissingRowsToItsAllowedTables()
    {
        var root = FindRepoRoot();
        var sql = ExtractSqlLiterals(File.ReadAllText(Path.Combine(root, TopUpPath)));
        var keyword = Regex.Match(sql, @"\b(?:CREATE|ALTER|DROP|UPDATE|DELETE|REPLACE|TRUNCATE|PRAGMA)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.False(
            keyword.Success,
            $"ReferenceDataTopUp must only add missing rows, but its SQL contains '{keyword.Value}'. Structure changes and data changes belong in a numbered upgrade step (spec decision D12).");

        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(statement => Regex.IsMatch(statement, @"\bINSERT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
        Assert.NotEmpty(statements);

        foreach (var statement in statements)
        {
            var orIgnore = Regex.Match(statement, @"^INSERT\s+OR\s+IGNORE\s+INTO\s+(?<table>\w+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var onConflict = Regex.Match(statement, @"^INSERT\s+INTO\s+(?<table>\w+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var doNothing = Regex.IsMatch(statement, @"\bON\s+CONFLICT\b[^;]*\bDO\s+NOTHING\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

            Assert.True(
                orIgnore.Success || (onConflict.Success && doNothing),
                $"ReferenceDataTopUp inserts must be 'INSERT OR IGNORE INTO <table>' or 'INSERT INTO <table> ... ON CONFLICT ... DO NOTHING', but found: {Preview(statement)}");

            var table = (orIgnore.Success ? orIgnore : onConflict).Groups["table"].Value;
            Assert.True(
                TopUpAllowedTables.Contains(table, StringComparer.OrdinalIgnoreCase),
                $"ReferenceDataTopUp inserts into '{table}', which is not one of {string.Join(", ", TopUpAllowedTables)}. Built-in rows for other tables belong in an upgrade step (spec decision D12).");
        }
    }

    private static string Preview(string statement)
    {
        var flat = Regex.Replace(statement, @"\s+", " ");
        return flat.Length <= 120 ? flat : flat[..120] + "...";
    }

    /// <summary>
    /// The text of every string literal in the file, comments removed, so the SQL can be checked without
    /// mistaking a word in a comment for a statement.
    /// </summary>
    private static string ExtractSqlLiterals(string source)
    {
        var withoutComments = string.Join('\n', source.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var literals = new StringBuilder();
        var remainder = Regex.Replace(
            withoutComments,
            "\"\"\"(?<body>.*?)\"\"\"",
            match =>
            {
                literals.Append(match.Groups["body"].Value).Append('\n');
                return string.Empty;
            },
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        foreach (Match match in Regex.Matches(remainder, "\"(?<body>(?:[^\"\\\\\\n]|\\\\.)*)\"", RegexOptions.CultureInvariant))
        {
            literals.Append(match.Groups["body"].Value).Append('\n');
        }

        return literals.ToString();
    }

    private static List<StepHash> ReadCurrentStepHashes(string root)
    {
        var upgradesDirectory = Path.Combine(root, UpgradesFolder);
        var hashes = new List<StepHash>();
        foreach (var step in DataStoreUpgradeCatalog.ForProduct())
        {
            if (step is DataStoreCodeUpgradeStep)
            {
                var typeName = step.GetType().Name;
                var files = Directory.GetFiles(upgradesDirectory, typeName + ".cs", SearchOption.AllDirectories);
                Assert.True(
                    files.Length == 1,
                    $"Code upgrade step {DataStoreUpgradeCatalog.Format(step.Number)} ({step.Name}) must live in exactly one file named {typeName}.cs under {UpgradesFolder}, but {files.Length} were found.");
                hashes.Add(new StepHash(step.Number, step.Name, Sha256Hex(Normalize(File.ReadAllText(files[0])))));
            }
            else
            {
                hashes.Add(new StepHash(step.Number, step.Name, step.Fingerprint));
            }
        }

        return hashes;
    }

    private static Dictionary<int, StepHash> ParseReleasedSteps(IEnumerable<string> lines)
    {
        var steps = new Dictionary<int, StepHash>();
        foreach (var line in lines.Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(
                parts.Length == 3 && int.TryParse(parts[0], out _),
                $"released-steps.txt has a line that is not 'NNNN name sha256': {line}");
            var number = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(steps.TryAdd(number, new StepHash(number, parts[1], parts[2])), $"released-steps.txt lists step {Format(number)} twice.");
        }

        return steps;
    }

    private static string RenderReleasedSteps(IEnumerable<StepHash> steps)
    {
        var text = new StringBuilder();
        text.Append("# Fingerprints of the data store upgrade steps, one line per step: NNNN name sha256.\n");
        text.Append("# A step that has been merged to main must never change. Maintained by: ").Append(RefreshCommand).Append('\n');
        foreach (var step in steps.OrderBy(step => step.Number))
        {
            text.Append(Format(step.Number)).Append(' ').Append(step.Name).Append(' ').Append(step.Hash).Append('\n');
        }

        return text.ToString();
    }

    private static Dictionary<string, string> ReadAllowlist()
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "MediaEngine.Storage.Tests", AllowlistFile);
        Assert.True(File.Exists(path), $"Missing {path}");

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var separator = line.IndexOf('|', StringComparison.Ordinal);
            var file = separator < 0 ? line : line[..separator].Trim();
            var reason = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
            Assert.True(reason.Length > 0, $"{AllowlistFile} entry '{file}' has no reason. Use 'path | reason'.");
            Assert.True(entries.TryAdd(file, reason), $"{AllowlistFile} lists '{file}' twice.");
        }

        return entries;
    }

    private static bool IsBuildOutput(string relativePath)
        => relativePath.Contains("/bin/", StringComparison.Ordinal) || relativePath.Contains("/obj/", StringComparison.Ordinal);

    private static string ReceivedPath(string approvedPath, string extension)
        => approvedPath[..^extension.Length] + ".received" + extension;

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string Normalize(string text)
        => text.TrimStart('﻿').ReplaceLineEndings("\n");

    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Format(int number)
        => DataStoreUpgradeCatalog.Format(number);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed record StepHash(int Number, string Name, string Hash);
}
