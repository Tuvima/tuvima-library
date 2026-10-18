namespace MediaEngine.Storage.Schema.Upgrades;

/// <summary>Base type for every data store upgrade failure so the Engine can catch one type at startup.</summary>
public abstract class DataStoreUpgradeException : InvalidOperationException
{
    protected DataStoreUpgradeException(string message)
        : base(message)
    {
    }

    protected DataStoreUpgradeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The set of known steps is malformed (gap, duplicate, bad name, forbidden statement).</summary>
internal sealed class DataStoreUpgradeCatalogException : DataStoreUpgradeException
{
    public DataStoreUpgradeCatalogException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The runner will not touch this data store: its record is damaged, it is newer than this Engine,
/// or an applied step no longer matches what was recorded.
/// </summary>
internal sealed class DataStoreUpgradeRefusedException : DataStoreUpgradeException
{
    public DataStoreUpgradeRefusedException(string message)
        : base(message)
    {
    }

    public DataStoreUpgradeRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A step failed. The data store was left as it was before that step.</summary>
internal sealed class DataStoreUpgradeFailedException : DataStoreUpgradeException
{
    public DataStoreUpgradeFailedException(
        int stepNumber,
        string stepName,
        string? safetyCopyPath,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        StepNumber = stepNumber;
        StepName = stepName;
        SafetyCopyPath = safetyCopyPath;
    }

    public int StepNumber { get; }

    public string StepName { get; }

    public string? SafetyCopyPath { get; }
}
