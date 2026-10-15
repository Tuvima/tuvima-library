using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests.Support;

/// <summary>Collects what components log so a test can prove a failure was recorded and not swallowed.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_sync)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._sync)
            {
                owner._entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
