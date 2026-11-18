namespace CashMovements.UseCases;

public sealed class LedgerService
{
    private readonly IMovementStore _movementStore;

    public LedgerService(IMovementStore store)
    {
        _movementStore = store;
    }

    public async Task<LedgerResult<Submission>> SubmitAsync(SubmitMovementInput? input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input is null)
            return LedgerResult<Submission>.Failure(LedgerError.InvalidMovement);

        if (!MovementRules.TryCanonicalIdentifier(input.AccountId, MovementRules.AccountIdMaxLength, out string canonicalAccountId) ||
            !MovementRules.TryCanonicalIdentifier(input.ExternalRef, MovementRules.ExternalRefMaxLength, out string canonicalExternalRef))
            return LedgerResult<Submission>.Failure(LedgerError.InvalidIdentifier);

        if (!CurrencyRules.HasValidCodeSyntax(input.Currency))
            return LedgerResult<Submission>.Failure(LedgerError.InvalidCurrency);

        string currency = input.Currency.ToUpperInvariant();
        if (!CurrencyRules.IsSupported(currency))
            return LedgerResult<Submission>.Failure(LedgerError.UnsupportedCurrency);
        if (!CurrencyRules.TryReadMovementAmount(input.Amount, currency, out decimal amount))
            return LedgerResult<Submission>.Failure(LedgerError.InvalidAmount);
        if (input.OccurredAtUtc is not DateTime { Kind: DateTimeKind.Utc } occurredAtUtc)
            return LedgerResult<Submission>.Failure(LedgerError.InvalidTimestamp);
        if (!MovementRules.IsValidNarration(input.Narration))
            return LedgerResult<Submission>.Failure(LedgerError.InvalidNarration);

        RecordedMovement movement = new RecordedMovement(
            canonicalAccountId, canonicalExternalRef, currency, amount, occurredAtUtc, input.Narration);
        return await _movementStore.RecordAsync(movement, cancellationToken);
    }

    public async Task<LedgerResult<AccountBalance>> GetBalanceAsync(string accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MovementRules.TryCanonicalIdentifier(accountId, MovementRules.AccountIdMaxLength, out string canonicalAccountId))
            return LedgerResult<AccountBalance>.Failure(LedgerError.InvalidIdentifier);

        return await _movementStore.GetBalanceAsync(canonicalAccountId, cancellationToken);
    }

    public async IAsyncEnumerable<LedgerResult<StatementEntry>> ExportStatementAsync(
        string accountId, DateTime? fromInclusiveUtc, DateTime? toExclusiveUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MovementRules.TryCanonicalIdentifier(accountId, MovementRules.AccountIdMaxLength, out string canonicalAccountId))
        {
            yield return LedgerResult<StatementEntry>.Failure(LedgerError.InvalidIdentifier);
            yield break;
        }

        if (fromInclusiveUtc is not DateTime { Kind: DateTimeKind.Utc } start ||
            toExclusiveUtc is not DateTime { Kind: DateTimeKind.Utc } end)
        {
            yield return LedgerResult<StatementEntry>.Failure(LedgerError.InvalidTimestamp);
            yield break;
        }

        if (start >= end)
        {
            yield return LedgerResult<StatementEntry>.Failure(LedgerError.InvalidRange);
            yield break;
        }

        await foreach (LedgerResult<StatementEntry> entry in
            _movementStore.ReadStatementAsync(canonicalAccountId, start, end, cancellationToken))
        {
            yield return entry;
        }
    }
}
