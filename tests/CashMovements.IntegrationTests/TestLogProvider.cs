namespace CashMovements.IntegrationTests;

// Observe the host through the same ILogger provider interface used by production log sinks.
internal sealed class TestLogProvider : ILoggerProvider
{
    private readonly Channel<LogEvent> logEvents = Channel.CreateUnbounded<LogEvent>();

    public ILogger CreateLogger(string categoryName)
    {
        return new CaptureLogger(categoryName, logEvents.Writer);
    }

    public void Dispose()
    {
        logEvents.Writer.TryComplete();
    }

    internal IEnumerable<LogEvent> Drain()
    {
        while (logEvents.Reader.TryRead(out LogEvent? logEvent))
            yield return logEvent;
    }

    internal async Task<LogEvent> WaitForOutcomeAsync(string outcome)
    {
        using CancellationTokenSource timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (LogEvent logEvent in logEvents.Reader.ReadAllAsync(timeoutSource.Token))
            if (logEvent.Fields.GetValueOrDefault("Outcome") == outcome)
                return logEvent;
        throw new InvalidOperationException($"The host stopped before logging {outcome}.");
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly string category;
        private readonly ChannelWriter<LogEvent> eventWriter;

        public CaptureLogger(string category, ChannelWriter<LogEvent> eventWriter)
        {
            this.category = category;
            this.eventWriter = eventWriter;
        }

        public bool IsEnabled(LogLevel level)
        {
            return level != LogLevel.None;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Dictionary<string, string?> logFields = state is IEnumerable<KeyValuePair<string, object?>> properties
                ? properties.ToDictionary(property => property.Key, property => property.Value?.ToString())
                : new Dictionary<string, string?>();
            eventWriter.TryWrite(new LogEvent(category, level, logFields, formatter(state, exception), exception));
        }
    }
}
