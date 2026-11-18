namespace CashMovements.IntegrationTests;

internal sealed record LogEvent(string Category, LogLevel Level, IReadOnlyDictionary<string, string?> Fields,
    string Message, Exception? Exception);
