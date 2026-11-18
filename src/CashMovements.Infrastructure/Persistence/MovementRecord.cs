namespace CashMovements.Infrastructure.Persistence;

internal sealed class MovementRecord
{
    public long MovementId { get; private set; }
    public required string AccountId { get; init; }
    public required string ExternalRef { get; init; }
    public required string Currency { get; init; }
    public required decimal Amount { get; init; }
    public required DateTime OccurredAtUtc { get; init; }
    public required string Narration { get; init; }
}
