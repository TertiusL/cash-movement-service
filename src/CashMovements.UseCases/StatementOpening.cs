namespace CashMovements.UseCases;

public sealed record StatementOpening(
    string AccountId,
    string Currency,
    DateTime FromInclusiveUtc,
    DateTime ToExclusiveUtc,
    decimal OpeningBalance) : StatementEntry;
