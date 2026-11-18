namespace CashMovements.UseCases;

public interface IMovementStore
{
    // Atomically record the movement or classify the winner for the same account and reference.
    Task<LedgerResult<Submission>> RecordAsync(RecordedMovement movement, CancellationToken cancellationToken);
    Task<LedgerResult<AccountBalance>> GetBalanceAsync(string accountId, CancellationToken cancellationToken);


    // One snapshot per enumeration. Yield the opening first, followed by movements in statement order.
    IAsyncEnumerable<LedgerResult<StatementEntry>> ReadStatementAsync(
        string accountId, DateTime fromInclusiveUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken);
}
