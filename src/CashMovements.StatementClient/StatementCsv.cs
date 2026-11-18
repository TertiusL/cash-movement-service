namespace CashMovements.StatementClient;

internal static class StatementCsv
{
    internal const string Columns = "recordType,accountId,externalRef,currency,amount,occurredAt,narration,runningBalance";
    private static readonly char[] QuotingCharacters = [',', '"', '\r', '\n'];

    internal static string SummaryRecord(string recordType, StatementHeader header, string balance)
    {
        return FormatRecord(recordType, header.AccountId, "", header.Currency, "", "", "", balance);
    }

    internal static string MovementRecord(StatementRow row)
    {
        Movement movement = row.Movement;
        if (movement?.OccurredAt is null)
        {
            throw new InvalidDataException("The statement row is missing its movement or timestamp.");
        }

        string occurredAt = movement.OccurredAt.ToDateTime().ToString("O", CultureInfo.InvariantCulture);
        return FormatRecord("movement", movement.AccountId, movement.ExternalRef, movement.Currency,
            movement.Amount, occurredAt, movement.Narration, row.RunningBalance);
    }

    private static string FormatRecord(params string[] fields)
    {
        return string.Join(',', fields.Select(EscapeField));
    }

    private static string EscapeField(string field)
    {
        if (field.IndexOfAny(QuotingCharacters) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
