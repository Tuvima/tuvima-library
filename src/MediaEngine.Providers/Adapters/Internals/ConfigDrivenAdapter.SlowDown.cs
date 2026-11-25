namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    /// <summary>
    /// HTTP 429 and 420 (Comic Vine's "velocity" answer) both mean "slow down and try again later",
    /// not "your request was wrong", so they never use up a job's attempts.
    /// </summary>
    private static bool IsSlowDownStatus(System.Net.HttpStatusCode status) =>
        status == System.Net.HttpStatusCode.TooManyRequests || (int)status == 420;
}
