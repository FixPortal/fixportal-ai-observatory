namespace AiObservatory.Data.Entities;

// OpenAI is the persisted and serialized provider identifier; changing its casing would
// break the established external contract.
// ReSharper disable once InconsistentNaming
public enum Provider { Anthropic, Copilot, Google, OpenAI, Moonshot }
