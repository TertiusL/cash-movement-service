namespace CashMovements.Infrastructure.Persistence;

internal sealed class AccountRecord
{
    public required string AccountId { get; init; }
    public required string Currency { get; init; }
}
