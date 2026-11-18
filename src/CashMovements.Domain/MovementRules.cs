namespace CashMovements.Domain;

public static class MovementRules
{
    public const int AccountIdMaxLength = 64;
    public const int ExternalRefMaxLength = 128;

    public static bool TryCanonicalIdentifier(string? input, int maxLength, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(input) || input.Length > maxLength)
            return false;

        bool hasLetterOrDigit = false;
        foreach (char character in input)
        {
            if (char.IsAsciiLetterOrDigit(character))
                hasLetterOrDigit = true;
            else if (character is not ('-' or '_' or '/'))
                return false;
        }

        if (!hasLetterOrDigit)
            return false;

        canonical = input.ToUpperInvariant();
        return true;
    }

    public static bool IsValidNarration(string? input)
    {
        return !string.IsNullOrWhiteSpace(input) && input.Length <= 256;
    }

    public static DateTime? ReadUtcInstant(long seconds, int nanos)
    {
        if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() ||
            seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ||
            nanos is < 0 or >= 1_000_000_000 || nanos % 100 != 0)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(nanos / 100);
    }
}
