namespace CashMovements.IntegrationTests;

internal sealed record StoredMovement(long Id, string AccountId, string ExternalRef, string Currency,
    decimal Amount, DateTime OccurredAtUtc, string Narration);
