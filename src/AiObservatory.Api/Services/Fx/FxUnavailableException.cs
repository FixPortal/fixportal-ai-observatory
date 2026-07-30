using NodaTime;

namespace AiObservatory.Api.Services.Fx;

// Thrown by FxRateProvider.GetGbpRateOnAsync when a non-USD, non-GBP rate cannot be
// resolved. Unlike USD, there is no static fallback for every other currency, so the
// ledger write must fail rather than freeze a wrong rate. Task 5's ledger write path
// catches this to record a per-row rejected verdict using the message below.
public class FxUnavailableException(string currency, LocalDate on)
    : Exception($"FX rate unavailable for {currency} on {on:yyyy-MM-dd}");
