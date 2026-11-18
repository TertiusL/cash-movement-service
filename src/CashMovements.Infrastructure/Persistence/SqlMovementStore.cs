namespace CashMovements.Infrastructure.Persistence;

public sealed class SqlMovementStore : IMovementStore
{
    private static readonly Expression<Func<MovementRecord, RecordedMovement>> MovementProjection =
        movement => new RecordedMovement(movement.AccountId, movement.ExternalRef, movement.Currency,
            movement.Amount, movement.OccurredAtUtc, movement.Narration);

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
            .Select(MovementProjection)
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
        return await ReadBalanceAsync(databaseContext, accountId, null, cancellationToken);
    }

    public async IAsyncEnumerable<LedgerResult<StatementEntry>> ReadStatementAsync(
        string accountId, DateTime fromInclusiveUtc, DateTime toExclusiveUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using CashMovementsDbContext databaseContext = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction snapshot = await databaseContext.Database.BeginTransactionAsync(
            IsolationLevel.Snapshot, cancellationToken);

        LedgerResult<AccountBalance> opening = await ReadBalanceAsync(databaseContext, accountId, fromInclusiveUtc, cancellationToken);
        if (opening.Error is LedgerError openingError)
        {
            yield return LedgerResult<StatementEntry>.Failure(openingError);
            yield break;
        }

        AccountBalance account = opening.Value;
        decimal runningBalance = account.Balance;
        yield return LedgerResult<StatementEntry>.Success(new StatementOpening(
            account.AccountId, account.Currency, fromInclusiveUtc, toExclusiveUtc, runningBalance));

        IQueryable<RecordedMovement> movements = databaseContext.Movements
            .TagWith("Statement movements")
            .AsNoTracking()
            .Where(movement => movement.AccountId == accountId &&
                movement.OccurredAtUtc >= fromInclusiveUtc && movement.OccurredAtUtc < toExclusiveUtc)
            .OrderBy(movement => movement.OccurredAtUtc)
            .ThenBy(movement => movement.MovementId)
            .Select(MovementProjection);

        await foreach (RecordedMovement movement in movements.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A supported balance plus a decimal(19,4) posting always fits .NET decimal exactly.
            // Enforce the narrower reporting range and minor unit before exposing the row.
            runningBalance = checked(runningBalance + movement.Amount);
            if (!CurrencyRules.IsReportableBalance(runningBalance, account.Currency))
            {
                yield return LedgerResult<StatementEntry>.Failure(LedgerError.AggregateOutOfRange);
                yield break;
            }
            yield return LedgerResult<StatementEntry>.Success(new StatementMovement(movement, runningBalance));
        }

        await snapshot.CommitAsync(cancellationToken);
    }

    private static async Task<LedgerResult<AccountBalance>> ReadBalanceAsync(
        CashMovementsDbContext databaseContext, string accountId, DateTime? beforeUtc, CancellationToken cancellationToken)
    {
        // SQL SUM widens to decimal(38,4). Narrow in SQL so a large result never
        // reaches a .NET decimal materializer. A null balance signals overflow.
        const string balanceJoin = """
            SELECT a.AccountId, a.Currency,
                   TRY_CONVERT(decimal(28,4), COALESCE(SUM(m.Amount), 0)) AS Balance
            FROM dbo.Accounts AS a
            LEFT JOIN dbo.Movements AS m ON m.AccountId = a.AccountId
            """;
        const string accountFilter = """

            WHERE a.AccountId = {0}
            GROUP BY a.AccountId, a.Currency
            """;
        // Keep the opening cutoff in the join so accounts with no earlier rows still return zero.
        IQueryable<BalanceRow> balanceQuery = beforeUtc is DateTime cutoff
            ? databaseContext.Database.SqlQueryRaw<BalanceRow>(
                balanceJoin + " AND m.OccurredAtUtc < {1}" + accountFilter, accountId, cutoff)
            : databaseContext.Database.SqlQueryRaw<BalanceRow>(balanceJoin + accountFilter, accountId);
        BalanceRow? accountBalance = await balanceQuery.SingleOrDefaultAsync(cancellationToken);

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
