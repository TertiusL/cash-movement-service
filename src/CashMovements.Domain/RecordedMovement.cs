namespace CashMovements.Domain;

// Record equality compares canonical keys, numeric amounts, UTC ticks and ordinal narration.
public sealed record RecordedMovement(
    string AccountId,
    string ExternalRef,
    string Currency,
    decimal Amount,
    DateTime OccurredAtUtc,
    string Narration);
