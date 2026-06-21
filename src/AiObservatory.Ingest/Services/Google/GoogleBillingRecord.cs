namespace AiObservatory.Ingest.Services.Google;

public record GoogleBillingRecord(
    string ServiceDescription,
    string Model,
    decimal CostUsd,
    string RawJson);
