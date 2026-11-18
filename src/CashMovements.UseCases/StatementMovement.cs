namespace CashMovements.UseCases;

public sealed record StatementMovement(RecordedMovement Movement, decimal RunningBalance) : StatementEntry;
