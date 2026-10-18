using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MediaEngine.Storage.Schema.Upgrades;

/// <summary>
/// Finds and validates the numbered upgrade steps: SQL steps from embedded <c>NNNN_short_name.sql</c>
/// resources and code steps from <see cref="DataStoreCodeUpgradeStep"/> subclasses, sharing one numbering.
/// </summary>
internal static class DataStoreUpgradeCatalog
{
    /// <summary>
    /// Prefix of the embedded SQL steps in this assembly (the <c>Schema/Upgrades</c> folder).
    /// </summary>
    public const string ProductResourcePrefix = "MediaEngine.Storage.Schema.Upgrades.";

    private static readonly Regex NamePattern = new("^[a-z0-9]+(_[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResourceFilePattern = new(
        "^(?<number>[0-9]{4})_(?<name>[a-z0-9]+(?:_[a-z0-9]+)*)\\.sql$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ForbiddenPragmaPattern = new(
        "\\bPRAGMA\\s+(?:[\\w\"`\\[\\]]+\\s*\\.\\s*)?[\"`\\[]?(?<pragma>journal_mode|foreign_keys)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The steps this build of the Engine ships with. An empty catalog is valid.</summary>
    public static IReadOnlyList<DataStoreUpgradeStep> ForProduct()
        => Load(typeof(DataStoreUpgradeCatalog).Assembly, ProductResourcePrefix);

    /// <summary>
    /// Discovers and validates the steps in <paramref name="assembly"/>.
    /// </summary>
    /// <param name="assembly">Assembly holding the embedded SQL resources and code step types.</param>
    /// <param name="resourcePrefix">Resource name prefix of the SQL steps, such as <c>MediaEngine.Storage.Schema.Upgrades.</c>.</param>
    /// <param name="codeStepNamespace">
    /// When given, only code steps whose namespace is, or sits under, this value are discovered.
    /// The product catalog passes <c>null</c> (the whole assembly).
    /// </param>
    /// <exception cref="DataStoreUpgradeCatalogException">The steps are malformed.</exception>
    public static IReadOnlyList<DataStoreUpgradeStep> Load(
        Assembly assembly,
        string resourcePrefix,
        string? codeStepNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);

        var prefix = resourcePrefix.EndsWith('.') ? resourcePrefix : resourcePrefix + ".";
        var steps = new List<DataStoreUpgradeStep>();
        steps.AddRange(LoadSqlSteps(assembly, prefix));
        steps.AddRange(LoadCodeSteps(assembly, codeStepNamespace));
        return Validate(steps);
    }

    /// <summary>
    /// Checks the steps and returns them in number order.
    /// Numbers must start at 1, be contiguous and be unique across both kinds; names must be well formed;
    /// SQL steps must not set <c>journal_mode</c> or <c>foreign_keys</c> (the runner owns those).
    /// </summary>
    /// <exception cref="DataStoreUpgradeCatalogException">The steps are malformed.</exception>
    public static IReadOnlyList<DataStoreUpgradeStep> Validate(IEnumerable<DataStoreUpgradeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var ordered = steps.OrderBy(step => step.Number).ThenBy(step => step.Source, StringComparer.Ordinal).ToList();

        foreach (var step in ordered)
        {
            if (string.IsNullOrEmpty(step.Name))
            {
                throw new DataStoreUpgradeCatalogException(
                    $"Upgrade step '{step.Source}' has no name. Names use lowercase letters and digits joined by single underscores.");
            }

            if (!NamePattern.IsMatch(step.Name))
            {
                throw new DataStoreUpgradeCatalogException(
                    $"Upgrade step '{step.Source}' has the name '{step.Name}'. Names use lowercase letters and digits joined by single underscores.");
            }

            if (step is SqlDataStoreUpgradeStep sql)
            {
                var transactionStatement = SqlScriptInspector.FindForbiddenStatementStart(sql.Script);
                if (transactionStatement is not null)
                {
                    throw new DataStoreUpgradeCatalogException(
                        $"Upgrade step '{step.Source}' has a {transactionStatement} statement. The runner owns the transaction (and a step cannot run VACUUM, ATTACH or DETACH inside it), so a step must not use BEGIN, COMMIT, END, ROLLBACK, SAVEPOINT, RELEASE, VACUUM, ATTACH or DETACH.");
                }

                var pragma = ForbiddenPragmaPattern.Match(sql.Script);
                if (pragma.Success)
                {
                    throw new DataStoreUpgradeCatalogException(
                        $"Upgrade step '{step.Source}' contains PRAGMA {pragma.Groups["pragma"].Value}. The runner owns journal_mode and foreign_keys; declare '{SqlDataStoreUpgradeStep.ForeignKeysOffMarker}' on the first line to run a step with foreign keys off.");
                }
            }
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var step = ordered[index];
            var expected = index + 1;
            if (index > 0 && ordered[index - 1].Number == step.Number)
            {
                throw new DataStoreUpgradeCatalogException(
                    $"Upgrade step number {Format(step.Number)} is used twice: '{ordered[index - 1].Source}' and '{step.Source}'.");
            }

            if (step.Number != expected)
            {
                throw new DataStoreUpgradeCatalogException(
                    $"Upgrade steps must be numbered 0001, 0002, 0003 with no gaps. Expected {Format(expected)} but found {Format(step.Number)} ('{step.Source}').");
            }
        }

        return ordered;
    }

    internal static string Format(int number)
        => number.ToString("D4", CultureInfo.InvariantCulture);

    private static IEnumerable<DataStoreUpgradeStep> LoadSqlSteps(Assembly assembly, string prefix)
    {
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                                    && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            var fileName = resourceName[prefix.Length..];
            var match = ResourceFilePattern.Match(fileName);
            if (!match.Success)
            {
                throw new DataStoreUpgradeCatalogException(
                    $"Upgrade step resource '{resourceName}' is not named NNNN_short_name.sql (four-digit number, then lowercase letters and digits joined by underscores).");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new DataStoreUpgradeCatalogException($"Upgrade step resource '{resourceName}' could not be opened.");
            using var reader = new StreamReader(stream);
            var script = reader.ReadToEnd();

            yield return new SqlDataStoreUpgradeStep(
                int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                script,
                resourceName);
        }
    }

    private static IEnumerable<DataStoreUpgradeStep> LoadCodeSteps(Assembly assembly, string? codeStepNamespace)
    {
        var types = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true } && type.IsSubclassOf(typeof(DataStoreCodeUpgradeStep)))
            .Where(type => codeStepNamespace is null
                           || type.Namespace is not null
                           && (type.Namespace.Equals(codeStepNamespace, StringComparison.Ordinal)
                               || type.Namespace.StartsWith(codeStepNamespace + ".", StringComparison.Ordinal)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is null)
            {
                throw new DataStoreUpgradeCatalogException(
                    $"Upgrade step type '{type.FullName}' needs a parameterless constructor.");
            }

            yield return (DataStoreUpgradeStep)Activator.CreateInstance(type, nonPublic: true)!;
        }
    }
}
