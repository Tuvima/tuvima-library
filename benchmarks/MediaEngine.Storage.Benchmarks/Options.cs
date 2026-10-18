namespace MediaEngine.Storage.Benchmarks;

/// <summary>Minimal <c>--name value</c> reader; unknown arguments are left for BenchmarkDotNet.</summary>
internal static class Options
{
    public static string Required(string[] args, string name) =>
        Optional(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

    public static string? Optional(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// <c>--writer-tps</c>: target writer transactions per second for the concurrency run. Defaults to 50 so readers on
    /// both stacks face the same write load; 0 means unthrottled. An unreadable or negative value is an error.
    /// </summary>
    public static double WriterTps(string[] args)
    {
        var text = Optional(args, "--writer-tps");
        if (text is null)
        {
            return 50;
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new ArgumentException($"--writer-tps must be a number of at least 0, not '{text}'.");
        }

        return value;
    }

    /// <summary>The arguments with one <c>--name value</c> pair removed.</summary>
    public static string[] Without(string[] args, string name)
    {
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal) && i + 1 < args.Length)
            {
                i++;
                continue;
            }

            result.Add(args[i]);
        }

        return [.. result];
    }
}
