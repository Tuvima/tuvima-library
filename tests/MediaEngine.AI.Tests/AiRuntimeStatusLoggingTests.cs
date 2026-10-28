using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Tests;

public sealed class AiRuntimeStatusLoggingTests
{
    [Fact]
    public void EmptyRuntimeFolder_IsOneInformationLine_NotAWarningOrAnException()
    {
        using var folder = new Scratch();
        var log = new Capture();
        var status = new AiRuntimeStatus(new SharedAiRuntime(new AiSettings { NativeRuntimeDirectory = folder.Path }, log.For<SharedAiRuntime>()), log.For<AiRuntimeStatus>());

        Assert.False(status.TextRuntimeLoads);
        status.RefreshTextRuntime();

        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(log.Entries, entry => entry.Exception is not null);
        var line = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains("Local AI is off", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptManifest_StillWarnsWithDetail()
    {
        using var folder = new Scratch();
        var component = Directory.CreateDirectory(Path.Combine(folder.Path, "llamasharp", SharedAiRuntime.LlamaVersion));
        File.WriteAllText(Path.Combine(component.FullName, "manifest.json"), "{ not json");
        var log = new Capture();
        var status = new AiRuntimeStatus(new SharedAiRuntime(new AiSettings { NativeRuntimeDirectory = folder.Path }, log.For<SharedAiRuntime>()), log.For<AiRuntimeStatus>());

        Assert.False(status.TextRuntimeLoads);

        var warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.NotNull(warning.Exception);
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tuvima-ai-status-" + Guid.NewGuid().ToString("N"));
        public Scratch() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private sealed class Capture
    {
        public List<Entry> Entries { get; } = [];
        public ILogger<T> For<T>() => new Logger<T>(Entries);

        private sealed class Logger<T>(List<Entry> entries) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }
    }
}
