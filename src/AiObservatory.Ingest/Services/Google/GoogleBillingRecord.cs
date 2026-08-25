using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public sealed record GoogleBillingRecord(
    LocalDate UsageDate,
    string BillingPeriod,
    string ServiceId,
    string ServiceDescription,
    string SkuId,
    string SkuDescription,
    string Currency,
    decimal GrossAmount,
    decimal CreditAmount,
    decimal NetAmount,
    Instant ObservedAt,
    string RawJson
);
