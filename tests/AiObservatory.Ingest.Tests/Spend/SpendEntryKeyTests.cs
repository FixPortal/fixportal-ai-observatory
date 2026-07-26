using AiObservatory.Data.Spend;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Spend;

/// <summary>
/// The occurrence index is load-bearing. Without it two genuine identical charges on the
/// same day collide and the second silently vanishes — a quiet under-count, which is the
/// failure class this project has already been burned by.
/// </summary>
public class SpendEntryKeyTests
{
    private static readonly LocalDate Date = new(2026, 7, 12);

    [Fact]
    public void SameInputsProduceTheSameKey()
    {
        var a = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var b = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);

        a.Should().Be(b, "re-importing the same file must be a no-op");
    }

    [Fact]
    public void OccurrenceIndexDistinguishesIdenticalCharges()
    {
        var first = SpendEntryKey.Derive(Date, "anthropic", 5.00m, "GBP", "Top-up", 0);
        var second = SpendEntryKey.Derive(Date, "anthropic", 5.00m, "GBP", "Top-up", 1);

        second.Should().NotBe(first, "two genuine identical charges must both survive");
    }

    [Theory]
    [InlineData("anthropic", 80.00, "GBP", "Top-up")]
    [InlineData("coderabbit", 80.00, "GBP", "Top-up")]   // vendor differs
    [InlineData("anthropic", 80.01, "GBP", "Top-up")]    // amount differs
    [InlineData("anthropic", 80.00, "USD", "Top-up")]    // currency differs
    [InlineData("anthropic", 80.00, "GBP", "Credits")]   // description differs
    public void EveryInputParticipatesInTheKey(string vendor, double amount, string currency, string description)
    {
        var baseline = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var candidate = SpendEntryKey.Derive(Date, vendor, (decimal)amount, currency, description, 0);

        if (vendor == "anthropic" && amount == 80.00 && currency == "GBP" && description == "Top-up")
        {
            candidate.Should().Be(baseline);
        }
        else
        {
            candidate.Should().NotBe(baseline);
        }
    }

    [Fact]
    public void DateParticipatesInTheKey()
    {
        var a = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "Top-up", 0);
        var b = SpendEntryKey.Derive(Date.PlusDays(1), "anthropic", 80.00m, "GBP", "Top-up", 0);

        b.Should().NotBe(a);
    }

    [Fact]
    public void NullAndEmptyDescriptionAreTheSame()
    {
        var withNull = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", null, 0);
        var withEmpty = SpendEntryKey.Derive(Date, "anthropic", 80.00m, "GBP", "", 0);

        withNull.Should().Be(withEmpty, "a blank CSV cell and an absent one are the same charge");
    }

    [Fact]
    public void KeyFitsTheColumn()
    {
        var key = SpendEntryKey.Derive(Date, new string('v', 500), 80.00m, "GBP", new string('d', 500), 0);

        key.Length.Should().BeLessThanOrEqualTo(200, "EntryKey is varchar(200)");
    }
}
