namespace CashMovements.UnitTests;

public sealed class MovementRulesTests
{
    [Theory]
    [InlineData("sTmt/001_a-0", "STMT/001_A-0")]
    [InlineData("001", "001")]
    [InlineData("-/z_", "-/Z_")]
    public void IdentifiersPreservePunctuationAndLeadingZeros(string input, string expected)
    {
        Assert.True(MovementRules.TryCanonicalIdentifier(input, MovementRules.AccountIdMaxLength, out string canonicalIdentifier));
        Assert.Equal(expected, canonicalIdentifier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-_/")]
    [InlineData(" A")]
    [InlineData("A ")]
    [InlineData("A\tB")]
    [InlineData("Å1")]
    [InlineData("A.1")]
    [InlineData("１２")]
    public void IdentifiersRejectInvalidCharactersBeforeUppercasing(string input)
    {
        Assert.False(MovementRules.TryCanonicalIdentifier(input, MovementRules.AccountIdMaxLength, out _));
    }

    [Theory]
    [InlineData(MovementRules.AccountIdMaxLength)]
    [InlineData(MovementRules.ExternalRefMaxLength)]
    public void IdentifierLimitsAreInclusive(int maximumLength)
    {
        Assert.True(MovementRules.TryCanonicalIdentifier(new string('a', maximumLength), maximumLength, out _));
        Assert.False(MovementRules.TryCanonicalIdentifier(new string('a', maximumLength + 1), maximumLength, out _));
    }

    [Theory]
    [InlineData("zar", true)]
    [InlineData("ZaR", true)]
    [InlineData("USD", true)]
    [InlineData(" JP", false)]
    [InlineData("ZAR ", false)]
    [InlineData("jp９", false)]
    [InlineData("ÅBC", false)]
    public void CurrencySyntaxRequiresThreeAsciiLetters(string input, bool expected)
    {
        Assert.Equal(expected, CurrencyRules.HasValidCodeSyntax(input));
    }

    [Theory]
    [InlineData("ZAR", "000125.500000", "125.50")]
    [InlineData("JPY", "-000001.0000", "-1")]
    [InlineData("BHD", "999999999999999.9990", "999999999999999.999")]
    [InlineData("BHD", "-999999999999999.999", "-999999999999999.999")]
    public void AmountsAcceptOnlyExactMinorUnitsAndFormatCanonically(string currency, string input, string expected)
    {
        Assert.True(CurrencyRules.TryReadMovementAmount(input, currency, out decimal amount));
        Assert.Equal(expected, CurrencyRules.Format(amount, currency));
    }

    [Theory]
    [InlineData("ZAR", "1.000000000000000000000000000000000001")]
    [InlineData("ZAR", "-0.001")]
    [InlineData("JPY", "125.1")]
    [InlineData("BHD", "125.0001")]
    [InlineData("ZAR", "-1000000000000000")]
    [InlineData("ZAR", "1000000000000000")]
    [InlineData("ZAR", "-0.000")]
    [InlineData("ZAR", "+1")]
    [InlineData("ZAR", "1e2")]
    [InlineData("ZAR", "1,000")]
    [InlineData("ZAR", "1.")]
    [InlineData("ZAR", ".1")]
    [InlineData("ZAR", "1.2.0")]
    [InlineData("ZAR", "1 ")]
    [InlineData("ZAR", " 1")]
    [InlineData("ZAR", "١")]
    public void InvalidAmountsFailBeforeDecimalConversionCanRoundThem(string currency, string input)
    {
        Assert.False(CurrencyRules.TryReadMovementAmount(input, currency, out _));
    }

    [Fact]
    public void InsignificantDigitsCanExceedDecimalPrecision()
    {
        string input = "-" + new string('0', 150) + "125.001" + new string('0', 150);
        Assert.True(CurrencyRules.TryReadMovementAmount(input, "BHD", out decimal amount));
        Assert.Equal(-125.001m, amount);
    }

    [Fact]
    public void AggregateLimitsApplyToBothSignsAndFractionalMinorUnits()
    {
        const decimal aggregateLimit = 1_000_000_000_000_000_000_000_000m;
        foreach (decimal sign in new[] { -1m, 1m })
        {
            Assert.True(CurrencyRules.IsReportableBalance(sign * (aggregateLimit - 0.001m), "BHD"));
            Assert.False(CurrencyRules.IsReportableBalance(sign * aggregateLimit, "BHD"));
            Assert.False(CurrencyRules.IsReportableBalance(sign * 1.001m, "ZAR"));
        }
        Assert.True(CurrencyRules.IsReportableBalance(0m, "JPY"));
    }

    [Fact]
    public void NarrationCountsUtf16UnitsAndRequiresNonwhitespace()
    {
        Assert.True(MovementRules.IsValidNarration("  Café\r\n"));
        Assert.False(MovementRules.IsValidNarration(" \t\u2003"));
        string maximumLengthNarration = new string('a', 254) + "😀";
        Assert.True(MovementRules.IsValidNarration(maximumLengthNarration));
        Assert.False(MovementRules.IsValidNarration(maximumLengthNarration + "a"));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 1)]
    [InlineData(-1, 999999999)]
    [InlineData(0, 1000000000)]
    [InlineData(-62135596801L, 0)]
    [InlineData(253402300800L, 0)]
    public void TimestampsRejectInvalidRangesAndSubtickPrecision(long seconds, int nanoseconds)
    {
        Assert.Null(MovementRules.ReadUtcInstant(seconds, nanoseconds));
    }

    [Fact]
    public void TimestampsPreserveTheFirstLastAndPreEpochTicks()
    {
        Assert.Equal(DateTime.MinValue.Ticks, MovementRules.ReadUtcInstant(-62135596800L, 0)!.Value.Ticks);
        Assert.Equal(DateTime.MaxValue.Ticks, MovementRules.ReadUtcInstant(253402300799L, 999999900)!.Value.Ticks);
        DateTime tickBeforeEpoch = MovementRules.ReadUtcInstant(-1, 999999900)!.Value;
        Assert.Equal(DateTimeKind.Utc, tickBeforeEpoch.Kind);
        Assert.Equal(DateTime.UnixEpoch.AddTicks(-1), tickBeforeEpoch);
    }
}
