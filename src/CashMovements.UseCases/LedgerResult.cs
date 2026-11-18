namespace CashMovements.UseCases;

public sealed class LedgerResult<T> where T : class
{
    private readonly T? _value;
    public LedgerError? Error { get; }

    public T Value
    {
        get
        {
            return _value ?? throw new InvalidOperationException("A failed result has no value.");
        }
    }

    private LedgerResult(T value)
    {
        _value = value;
    }

    private LedgerResult(LedgerError error)
    {
        Error = error;
    }

    public static LedgerResult<T> Success(T value)
    {
        return new LedgerResult<T>(value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static LedgerResult<T> Failure(LedgerError error)
    {
        return new LedgerResult<T>(error);
    }
}
