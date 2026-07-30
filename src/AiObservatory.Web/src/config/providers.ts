// Single source of truth for all provider metadata.
// Add new providers here — consuming modules derive from this list automatically.

export const PROVIDER_KEYS = ['anthropic', 'copilot', 'google', 'openai', 'moonshot'] as const
export type ProviderKey = typeof PROVIDER_KEYS[number]

export interface ProviderConfig {
  key: ProviderKey
  displayName: string
  colorVar: string
  // Estimated savings per cache-read token (USD). Can be a number, a record of model name substrings to rates,
  // or null if the provider does not surface cache-read tokens or has no known caching discount.
  cacheSavingsPerToken: number | Record<string, number> | null
  badgeStyle: { color: string; background: string }
}

export const PROVIDERS = [
  {
    key: 'anthropic',
    displayName: 'Anthropic',
    colorVar: 'var(--provider-anthropic)',
    // Savings per cache-read token = (input rate - cache-read rate), USD per token.
    // Keys are matched as substrings, LONGEST KEY FIRST (see getCacheSavingsRate in
    // SummaryCards.tsx), so a specific tier beats its family.
    //
    // KNOWN LIMITATION — this is a hand-maintained duplicate of the backend rate table in
    // src/AiObservatory.Ingest/appsettings.json, and it has no date dimension, so it cannot
    // express a time-bounded price. The 'sonnet-5' entry below is the introductory rate and
    // goes stale on 2026-09-01. The real fix is to source these from the backend rather than
    // restate them here; until then, changing a rate means changing it in both places.
    // Rates verified against https://platform.claude.com/docs/en/about-claude/pricing (2026-07-25).
    cacheSavingsPerToken: {
      // Current Opus tiers: $5.00 - $0.50
      'opus-5': 0.0000045,
      'opus-4-8': 0.0000045,
      'opus-4-7': 0.0000045,
      'opus-4-6': 0.0000045,
      'opus-4-5': 0.0000045,
      // Legacy Opus (4, 4.1, 3-opus): $15.00 - $1.50
      'opus': 0.0000135,
      // Fable/Mythos 5: $10.00 - $1.00
      'fable-5': 0.000009,
      'mythos-5': 0.000009,
      // Sonnet 5 introductory pricing through 2026-08-31: $2.00 - $0.20.
      // Reverts to the 'sonnet' rate below on 2026-09-01 — this entry must be removed then.
      'sonnet-5': 0.0000018,
      // Sonnet 4.x: $3.00 - $0.30
      'sonnet': 0.0000027,
      // Haiku 4.5: $1.00 - $0.10
      'haiku-4-5': 0.0000009,
      'haiku-3-5': 0.00000072, // $0.72 per 1M ($0.80 - $0.08 input vs cache read rate)
      'haiku-3': 0.00000022,   // $0.22 per 1M ($0.25 - $0.03 input vs cache read rate)
      'claude-3-haiku': 0.00000022,
      'haiku': 0.00000072,
      'default': 0.0000027,
    },
    badgeStyle: { color: 'var(--provider-anthropic)', background: 'rgba(124,58,237,.12)' },
  },
  {
    key: 'copilot',
    displayName: 'Copilot',
    colorVar: 'var(--provider-copilot)',
    cacheSavingsPerToken: null, // Deliberate null: Copilot is subscription-billed, so there is no real cache pricing discount (see F6).
    badgeStyle: { color: 'var(--provider-copilot)', background: 'rgba(219,39,119,.12)' },
  },
  {
    key: 'google',
    displayName: 'Google',
    colorVar: 'var(--provider-google)',
    cacheSavingsPerToken: 0.000001125, // $1.125 per 1M (Gemini basis)
    badgeStyle: { color: 'var(--provider-google)', background: 'rgba(2,132,199,.12)' },
  },
  {
    key: 'openai',
    displayName: 'OpenAI',
    colorVar: 'var(--provider-openai)',
    cacheSavingsPerToken: 0.00000125,  // $1.25 per 1M (gpt-4o basis: 50% discount on $2.50/1M input)
    badgeStyle: { color: 'var(--provider-openai)', background: 'rgba(234,88,12,.12)' },
  },
  {
    key: 'moonshot',
    displayName: 'Moonshot',
    colorVar: 'var(--provider-moonshot)',
    // Deliberate null, same rationale as Copilot: Kimi Code authenticates through the
    // Allegretto OAuth subscription (flat rate), not metered Moonshot API credits, so a
    // cache read saves no money — only context. Tokens are tracked for utilisation.
    cacheSavingsPerToken: null,
    badgeStyle: { color: 'var(--provider-moonshot)', background: 'rgba(101,163,13,.12)' },
  },
] satisfies ProviderConfig[]

/** Stable display order for provider filter chips and dropdowns. */
export const PROVIDER_ORDER: ProviderKey[] = PROVIDERS.map(p => p.key)

export function getProvider(key: string): ProviderConfig | undefined {
  return PROVIDERS.find(p => p.key === key)
}
