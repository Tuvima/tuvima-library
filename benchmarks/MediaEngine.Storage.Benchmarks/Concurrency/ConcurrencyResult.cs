namespace MediaEngine.Storage.Benchmarks.Concurrency;

/// <summary>Latency summary for one kind of reader operation.</summary>
internal sealed record OperationStats(string Operation, int Count, double P50Ms, double P95Ms, double P99Ms, double MaxMs);

/// <summary>Everything one scenario/stack run reports. Produced by the child process, printed and exported by the parent.</summary>
internal sealed record ConcurrencyResult(
    string Scenario,
    string Stack,
    double Seconds,
    double WriterTargetTps,
    double WriterSeconds,
    IReadOnlyList<OperationStats> ReaderOperations,
    int WriterTransactions,
    int WriterBusyErrors,
    int ReaderBusyErrors,
    int OtherErrors,
    IReadOnlyList<string> ErrorSamples,
    double PeakWorkingSetMiB,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double AllocatedMiB,
    double WalFileMiB);
