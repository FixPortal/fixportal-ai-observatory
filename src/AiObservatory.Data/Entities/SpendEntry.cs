using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// One billed charge. Deliberately carries NO account, card, counterparty, invoice number
/// or bank transaction id — see spec §3, enforced by
/// <c>ArchitectureTests.SpendEntry_must_not_carry_bank_linkage</c>.
/// </summary>
public sealed class SpendEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The charge date. Drives both the reporting period and the FX rate used.</summary>
    public LocalDate OccurredOn { get; set; }

    public Guid VendorId { get; set; }
    public Guid CategoryId { get; set; }

    /// <summary>Amount as charged, in <see cref="Currency"/>.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217, upper case.</summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>
    /// <see cref="Amount"/> in GBP, converted once at write using the rate on
    /// <see cref="OccurredOn"/> and never recomputed. Totals sum this column. Converting at
    /// render instead — the convention used for token costs — would make a historical
    /// charge show a different figure every day and an annual total drift with the market.
    /// </summary>
    public decimal AmountGbp { get; set; }

    /// <summary>The rate actually applied, so every conversion is auditable. 1 when
    /// <see cref="Currency"/> is GBP.</summary>
    public decimal FxRate { get; set; }

    public string? Description { get; set; }

    public SpendSource Source { get; set; }

    /// <summary>
    /// Idempotency key, unique per source. Null for manual entries: a person typing the
    /// same charge twice is a mistake worth showing them, not one to silence.
    /// </summary>
    public string? EntryKey { get; set; }

    public Instant RecordedAt { get; set; }
}
