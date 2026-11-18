namespace CashMovements.Infrastructure.Persistence;

public sealed class SqlMovementStore : IMovementStore
{
    private readonly IDbContextFactory<CashMovementsDbContext> _contextFactory;

    public SqlMovementStore(IDbContextFactory<CashMovementsDbContext> contexts)
    {
        _contextFactory = contexts;
    }

    public async Task<LedgerResult<Submission>> RecordAsync(
        RecordedMovement movement, CancellationToken cancellationToken)
    {
        try
        {
            await using CashMovementsDbContext databaseContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
            string? accountCurrency = await databaseContext.Accounts
                .Where(account => account.AccountId == movement.AccountId)
                .Select(account => account.Currency)
                .SingleOrDefaultAsync(cancellationToken);
            if (accountCurrency is null)
                return LedgerResult<Submission>.Failure(LedgerError.AccountNotFound);

            // A different supported currency on an existing key is a conflicting retry.
            RecordedMovement? existingMovement = await FindMovementAsync(databaseContext, movement, cancellationToken);
            if (existingMovement is not null)
                return ClassifyRetry(existingMovement, movement);
            if (accountCurrency != movement.Currency)
                return LedgerResult<Submission>.Failure(LedgerError.AccountCurrencyMismatch);

            databaseContext.Movements.Add(new MovementRecord
            {
                AccountId = movement.AccountId,
                ExternalRef = movement.ExternalRef,
                Currency = movement.Currency,
                Amount = movement.Amount,
                OccurredAtUtc = movement.OccurredAtUtc,
                Narration = movement.Narration
            });
            await databaseContext.SaveChangesAsync(cancellationToken);
            return LedgerResult<Submission>.Success(new Submission(RecordDisposition.Recorded, movement));
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // A competing insert can commit after our lookup. Read the winner after the
            // failed write context is disposed, and propagate an unconfirmed key violation.
            await using CashMovementsDbContext databaseContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
            RecordedMovement? committedMovement = await FindMovementAsync(databaseContext, movement, cancellationToken);
            if (committedMovement is null)
                throw;
            return ClassifyRetry(committedMovement, movement);
        }
    }

    private static Task<RecordedMovement?> FindMovementAsync(
        CashMovementsDbContext databaseContext, RecordedMovement movement, CancellationToken cancellationToken)
    {
        return databaseContext.Movements.AsNoTracking()
            .Where(stored => stored.AccountId == movement.AccountId && stored.ExternalRef == movement.ExternalRef)
            .Select(stored => new RecordedMovement(
                stored.AccountId, stored.ExternalRef, stored.Currency, stored.Amount, stored.OccurredAtUtc, stored.Narration))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static LedgerResult<Submission> ClassifyRetry(RecordedMovement storedMovement, RecordedMovement submittedMovement)
    {
        return storedMovement == submittedMovement
            ? LedgerResult<Submission>.Success(new Submission(RecordDisposition.AlreadyRecorded, storedMovement))
            : LedgerResult<Submission>.Failure(LedgerError.Conflict);
    }

    public async Task<LedgerResult<AccountBalance>> GetBalanceAsync(string accountId, CancellationToken cancellationToken)
    {
        await using CashMovementsDbContext databaseContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
        // SQL SUM widens to decimal(38,4). Narrow in SQL so a large result never
        // reaches a .NET decimal materializer. A null balance signals overflow.
        BalanceRow? accountBalance = await databaseContext.Database.SqlQuery<BalanceRow>($"""
            SELECT a.AccountId, a.Currency,
                   TRY_CONVERT(decimal(28,4), COALESCE(SUM(m.Amount), 0)) AS Balance
            FROM dbo.Accounts AS a
            LEFT JOIN dbo.Movements AS m ON m.AccountId = a.AccountId
            WHERE a.AccountId = {accountId}
            GROUP BY a.AccountId, a.Currency
            """).SingleOrDefaultAsync(cancellationToken);

        if (accountBalance is null)
            return LedgerResult<AccountBalance>.Failure(LedgerError.AccountNotFound);
        if (accountBalance.Balance is not decimal balance || !CurrencyRules.IsReportableBalance(balance, accountBalance.Currency))
            return LedgerResult<AccountBalance>.Failure(LedgerError.AggregateOutOfRange);

        return LedgerResult<AccountBalance>.Success(new AccountBalance(accountBalance.AccountId, accountBalance.Currency, balance));
    }

    private sealed class BalanceRow
    {
        public required string AccountId { get; init; }
        public required string Currency { get; init; }
        public decimal? Balance { get; init; }
    }
}
