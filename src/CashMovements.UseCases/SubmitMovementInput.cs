namespace CashMovements.UseCases;

public sealed record SubmitMovementInput(
    string AccountId,
    string ExternalRef,
    string Currency,
    string Amount,
    DateTime? OccurredAtUtc,
    string Narration);
