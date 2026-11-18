namespace CashMovements.UseCases;

public enum LedgerError
{
    InvalidMovement,
    InvalidIdentifier,
    InvalidCurrency,
    UnsupportedCurrency,
    InvalidAmount,
    InvalidTimestamp,
    InvalidNarration,
    AccountNotFound,
    Conflict,
    AccountCurrencyMismatch,
    AggregateOutOfRange,
    InvalidRange
}
