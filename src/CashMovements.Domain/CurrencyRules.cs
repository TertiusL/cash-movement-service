namespace CashMovements.Domain;

public static class CurrencyRules
{
    // A decimal(19,4) movement has fifteen integral digits; decimal(28,4) totals have twenty-four.
    private const int MaxMovementIntegralDigits = 15;
    private const decimal AggregateLimit = 1_000_000_000_000_000_000_000_000m;

    public static bool HasValidCodeSyntax(string? code)
    {
        return code is { Length: 3 } && code.All(char.IsAsciiLetter);
    }

    public static bool IsSupported(string currency)
    {
        return MinorUnitDigits(currency).HasValue;
    }

    public static bool TryReadMovementAmount(string? text, string currency, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrEmpty(text) || MinorUnitDigits(currency) is not int minorUnitDigits)
            return false;

        ReadOnlySpan<char> unsignedAmount = text.AsSpan(text[0] == '-' ? 1 : 0);
        int decimalPointIndex = unsignedAmount.IndexOf('.');
        ReadOnlySpan<char> wholeNumberDigits = decimalPointIndex < 0 ? unsignedAmount : unsignedAmount[..decimalPointIndex];
        ReadOnlySpan<char> fractionalDigits = decimalPointIndex < 0
            ? ReadOnlySpan<char>.Empty
            : unsignedAmount[(decimalPointIndex + 1)..];
        if (wholeNumberDigits.IsEmpty || (decimalPointIndex >= 0 && fractionalDigits.IsEmpty) ||
            wholeNumberDigits.ContainsAnyExceptInRange('0', '9') || fractionalDigits.ContainsAnyExceptInRange('0', '9'))
            return false;

        // Inspect significant digits first. Decimal.TryParse can silently round excessive precision.
        if (wholeNumberDigits.TrimStart('0').Length > MaxMovementIntegralDigits ||
            fractionalDigits.TrimEnd('0').Length > minorUnitDigits)
            return false;

        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        return decimal.TryParse(text, style, CultureInfo.InvariantCulture, out amount) && amount != 0m;
    }

    public static bool IsReportableBalance(decimal amount, string currency)
    {
        return amount > -AggregateLimit && amount < AggregateLimit &&
            MinorUnitDigits(currency) is int minorUnitDigits && decimal.Round(amount, minorUnitDigits) == amount;
    }

    public static string Format(decimal amount, string currency)
    {
        int minorUnitDigits = MinorUnitDigits(currency) ?? throw new ArgumentOutOfRangeException(nameof(currency));
        return amount.ToString($"F{minorUnitDigits}", CultureInfo.InvariantCulture);
    }

    private static int? MinorUnitDigits(string currency)
    {
        return currency switch
        {
            "ZAR" => 2,
            "JPY" => 0,
            "BHD" => 3,
            _ => null
        };
    }
}
