namespace CashMovements.UseCases;

public interface IMovementStore
{
    // Atomically record the movement or classify the winner for the same account and reference.
    Task<LedgerResult<Submission>> RecordAsync(RecordedMovement movement, CancellationToken cancellationToken);
    Task<LedgerResult<AccountBalance>> GetBalanceAsync(string accountId, CancellationToken cancellationToken);
}
