namespace CashMovements.UseCases;

public sealed record AccountBalance(string AccountId, string Currency, decimal Balance);
