namespace CashMovements.IntegrationTests;

internal sealed class StatementCommitFault : DbTransactionInterceptor
{
    internal bool Enabled { get; set; }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Enabled && transaction.IsolationLevel == IsolationLevel.Snapshot)
        {
            throw new InvalidOperationException("Injected failure after all statement rows were sent.");
        }
        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }
}
