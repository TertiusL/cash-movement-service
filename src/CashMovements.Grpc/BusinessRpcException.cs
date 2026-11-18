namespace CashMovements.Grpc;

internal sealed class BusinessRpcException : RpcException
{
    public LedgerError Reason { get; }

    private BusinessRpcException(LedgerError reason, StatusCode status, BusinessErrorReason wireReason)
        : base(new Status(status, reason.ToString()),
            new Metadata { { "cash-error-bin", new BusinessError { Reason = wireReason }.ToByteArray() } })
    {
        Reason = reason;
    }

    public static BusinessRpcException From(LedgerError reason)
    {
        (StatusCode status, BusinessErrorReason wireReason) = reason switch
        {
            LedgerError.InvalidMovement => (StatusCode.InvalidArgument, BusinessErrorReason.InvalidMovement),
            LedgerError.InvalidIdentifier => (StatusCode.InvalidArgument, BusinessErrorReason.InvalidIdentifier),
            LedgerError.InvalidCurrency => (StatusCode.InvalidArgument, BusinessErrorReason.InvalidCurrency),
            LedgerError.UnsupportedCurrency => (StatusCode.InvalidArgument, BusinessErrorReason.UnsupportedCurrency),
            LedgerError.InvalidAmount => (StatusCode.InvalidArgument, BusinessErrorReason.InvalidAmount),
            LedgerError.InvalidTimestamp => (StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp),
            LedgerError.InvalidNarration => (StatusCode.InvalidArgument, BusinessErrorReason.InvalidNarration),
            LedgerError.AccountNotFound => (StatusCode.NotFound, BusinessErrorReason.AccountNotFound),
            LedgerError.Conflict => (StatusCode.AlreadyExists, BusinessErrorReason.Conflict),
            LedgerError.AccountCurrencyMismatch => (StatusCode.FailedPrecondition, BusinessErrorReason.AccountCurrencyMismatch),
            LedgerError.AggregateOutOfRange => (StatusCode.OutOfRange, BusinessErrorReason.AggregateOutOfRange),
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        return new BusinessRpcException(reason, status, wireReason);
    }
}
