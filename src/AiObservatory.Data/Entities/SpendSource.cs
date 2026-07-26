namespace AiObservatory.Data.Entities;

/// <summary>How a <see cref="SpendEntry"/> reached the ledger. Provenance only — never a bank reference.</summary>
public enum SpendSource { Manual, Csv, Portal }
