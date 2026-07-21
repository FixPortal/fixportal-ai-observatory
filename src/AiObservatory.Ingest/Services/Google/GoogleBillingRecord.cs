namespace AiObservatory.Ingest.Services.Google;

// Provider payload properties are populated and consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public record GoogleBillingRecord(
    string ServiceDescription,
    string Model,
    decimal CostUsd,
    string RawJson);
