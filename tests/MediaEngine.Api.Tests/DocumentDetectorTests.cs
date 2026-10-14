using MediaEngine.Api.Services.LocalAssets.Filing;

namespace MediaEngine.Api.Tests;

public sealed class DocumentDetectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    [Theory]
    [InlineData("Invoice date: 2026-03-04", 2026, 3, 4)]
    [InlineData("Issued 2026/3/4 by the office", 2026, 3, 4)]
    [InlineData("Date 4 March 2026", 2026, 3, 4)]
    [InlineData("Date 4th of March, 2026", 2026, 3, 4)]
    [InlineData("March 4, 2026", 2026, 3, 4)]
    [InlineData("Dated Sept 9 2025", 2025, 9, 9)]
    [InlineData("on 12-Jan-2026 we", 2026, 1, 12)]
    [InlineData("Datum 25.12.2025", 2025, 12, 25)]
    [InlineData("posted 12/25/2025", 2025, 12, 25)]
    public void FindsUnambiguousDatesInEveryCommonShape(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), DocumentDateDetector.Detect(text, Today));
    }

    [Fact]
    public void ADateThatCouldBeReadEitherWayIsOnlyUsedWhenThePreferenceIsKnown()
    {
        Assert.Null(DocumentDateDetector.Detect("Invoice date 03/04/2026", Today));
        Assert.Equal(new DateOnly(2026, 4, 3), DocumentDateDetector.Detect("Invoice date 03/04/2026", Today, DocumentDateOrder.DayFirst));
        Assert.Equal(new DateOnly(2026, 3, 4), DocumentDateDetector.Detect("Invoice date 03/04/2026", Today, DocumentDateOrder.MonthFirst));
        // Same number twice has only one reading.
        Assert.Equal(new DateOnly(2026, 5, 5), DocumentDateDetector.Detect("05/05/2026", Today));
    }

    [Fact]
    public void AnUnambiguousDateWinsOverAnAmbiguousOne()
    {
        Assert.Equal(new DateOnly(2026, 2, 20), DocumentDateDetector.Detect("Ref 03/04/2026 issued 20 February 2026", Today));
    }

    [Fact]
    public void DatesNearIssueWordsBeatDueDatesAndEarlierDates()
    {
        const string text = "Customer since 2019-01-15\nDue date: 2026-04-30\nInvoice date: 2026-03-31\nPay within 30 days";
        Assert.Equal(new DateOnly(2026, 3, 31), DocumentDateDetector.Detect(text, Today));
    }

    [Fact]
    public void WithoutAnyKeywordTheFirstDateInTheTextIsUsed()
    {
        Assert.Equal(new DateOnly(2026, 1, 2), DocumentDateDetector.Detect("Statement\n2026-01-02\nand later 2026-02-03", Today));
    }

    [Fact]
    public void BirthDueAndExpiryDatesAreNeverTheDocumentDate()
    {
        Assert.Equal(new DateOnly(2026, 6, 1), DocumentDateDetector.Detect("Date of birth 1985-02-11\nDated 2026-06-01", Today));
        Assert.Null(DocumentDateDetector.Detect("Date of birth 1985-02-11", Today));
        Assert.Null(DocumentDateDetector.Detect("Payment due 2026-04-30", Today));
        Assert.Null(DocumentDateDetector.Detect("Valid until 2026-12-31", Today));
    }

    [Theory]
    [InlineData("Invoice date: 1950-01-01")]
    [InlineData("Invoice date: 2030-01-01")]
    [InlineData("Invoice date: 2026-02-30")]
    [InlineData("Invoice date: 2026-13-01")]
    [InlineData("Phone 555-1234")]
    [InlineData("Server 192.168.1.100 responded")]
    [InlineData("Version 1.2.2026b")]
    [InlineData("Page 12/2026")]
    [InlineData("Order 20260304")]
    [InlineData("")]
    [InlineData(null)]
    public void FalsePositivesAndImpossibleDatesAreIgnored(string? text)
    {
        Assert.Null(DocumentDateDetector.Detect(text, Today));
    }

    [Fact]
    public void ADateMoreThanFiftyYearsBackOrAYearAheadIsIgnoredButTheEdgesCount()
    {
        Assert.Equal(new DateOnly(2027, 10, 14), DocumentDateDetector.Detect("2027-10-14", Today));
        Assert.Null(DocumentDateDetector.Detect("2027-10-15", Today));
        Assert.Equal(new DateOnly(1976, 10, 14), DocumentDateDetector.Detect("1976-10-14", Today));
        Assert.Null(DocumentDateDetector.Detect("1976-10-13", Today));
    }

    [Theory]
    [InlineData("Amount due: 120.50 EUR", 120.50, "EUR")]
    [InlineData("TOTAL DUE $1,234.56", 1234.56, "USD")]
    [InlineData("Grand total € 99,00", 99.00, "EUR")]
    [InlineData("Balance due: £45.10", 45.10, "GBP")]
    [InlineData("Total 1.234,56 EUR", 1234.56, "EUR")]
    [InlineData("Amount due 75.00", 75.00, null)]
    public void ReadsTheAmountNextToAKnownLabel(string text, double amount, string? currency)
    {
        var found = DocumentAmountDetector.Detect(text);
        Assert.NotNull(found);
        Assert.Equal(amount, found!.Amount, 2);
        Assert.Equal(currency, found.Currency);
    }

    [Fact]
    public void AStrongerLabelWinsOverTotal()
    {
        const string text = "Total 50.00 EUR\nAmount due 20.00 EUR";
        Assert.Equal(20.00, DocumentAmountDetector.Detect(text)!.Amount, 2);
    }

    [Theory]
    [InlineData("Subtotal 100.00 EUR")]
    [InlineData("Total 2026")]
    [InlineData("Total items 12")]
    [InlineData("Amount due 5 000 pieces")]
    [InlineData("Receipt no 4471")]
    [InlineData("Total 10.00 EUR\nTotal 11.00 EUR")]
    [InlineData("Total 0.00 EUR")]
    [InlineData("")]
    [InlineData(null)]
    public void StaysQuietWhenTheAmountIsNotClear(string? text)
    {
        Assert.Null(DocumentAmountDetector.Detect(text));
    }

    [Fact]
    public void ARepeatedSameTotalIsStillOneAmount()
    {
        Assert.Equal(60.00, DocumentAmountDetector.Detect("Total 60.00 EUR ... Total 60.00 EUR")!.Amount, 2);
    }

    [Theory]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("12,50", 12.5)]
    [InlineData("1,234", 1234)]
    [InlineData("0.123", 0.123)]
    [InlineData("1234.5", 1234.5)]
    public void NumbersAreParsedWhicheverWayTheyAreWritten(string text, double expected)
    {
        Assert.True(DocumentAmountDetector.TryParse(text, out var amount));
        Assert.Equal(expected, amount, 3);
    }
}
