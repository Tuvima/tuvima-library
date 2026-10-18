using System.Diagnostics;

namespace MediaEngine.Storage.Benchmarks.Verification;

/// <summary>One query/parameter combination: the production result and every implemented variant to compare to it.</summary>
internal sealed record VerifyCase(
    string Query,
    string Parameters,
    Func<Task<IReadOnlyList<string>>> Production,
    IReadOnlyList<VerifyVariant> Variants,
    IReadOnlyList<string> NotPractical);

internal sealed record VerifyVariant(string Name, Func<Task<IReadOnlyList<string>>> Run);

/// <summary>
/// Runs every query through every implemented variant once and compares each against the production Dapper result:
/// same row count, same ordered rows, and equality of every mapped field (rows are compared as full-field text).
/// Any mismatch fails with the first differing row.
/// </summary>
internal static class Verifier
{
    public static async Task<bool> RunAsync(IReadOnlyList<VerifyCase> cases)
    {
        var failures = 0;
        foreach (var verifyCase in cases)
        {
            Console.WriteLine($"{verifyCase.Query} [{verifyCase.Parameters}]");
            var timer = Stopwatch.StartNew();
            var expected = await verifyCase.Production();
            Console.WriteLine($"    production (Dapper): {expected.Count:N0} lines in {timer.Elapsed.TotalMilliseconds:N0} ms");
            if (expected.Count == 0)
            {
                Console.WriteLine("    FAIL: the production result is empty; the dataset does not exercise this query.");
                failures++;
                continue;
            }

            foreach (var variant in verifyCase.Variants)
            {
                timer.Restart();
                IReadOnlyList<string> actual;
                try
                {
                    actual = await variant.Run();
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"    FAIL {variant.Name}: threw {exception.GetType().Name}: {exception.Message}");
                    if (Environment.GetEnvironmentVariable("BENCH_VERBOSE") == "1")
                    {
                        Console.WriteLine(exception);
                    }

                    failures++;
                    continue;
                }

                var mismatch = FirstMismatch(expected, actual);
                if (mismatch is null)
                {
                    Console.WriteLine($"    ok   {variant.Name}: {actual.Count:N0} lines identical ({timer.Elapsed.TotalMilliseconds:N0} ms)");
                }
                else
                {
                    Console.WriteLine($"    FAIL {variant.Name}: {mismatch}");
                    failures++;
                }
            }

            foreach (var reason in verifyCase.NotPractical)
            {
                Console.WriteLine($"    n/a  {reason}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "verify passed: every implemented variant returned results identical to production."
            : $"verify FAILED: {failures} variant(s) differ from production.");
        return failures == 0;
    }

    private static string? FirstMismatch(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var shared = Math.Min(expected.Count, actual.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                return $"line {i} differs.{Environment.NewLine}      production: {Trim(expected[i])}{Environment.NewLine}      variant:    {Trim(actual[i])}";
            }
        }

        return expected.Count == actual.Count
            ? null
            : $"row count differs: production {expected.Count:N0} lines, variant {actual.Count:N0} lines (first {shared:N0} identical).";
    }

    private static string Trim(string text) => text.Length <= 600 ? text : text[..600] + "...";
}
