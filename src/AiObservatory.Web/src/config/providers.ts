// Single source of truth for all provider metadata.
// Add new providers here — consuming modules derive from this list automatically.

export interface ProviderConfig {
  key: string
  displayName: string
  colorVar: string
  // Estimated savings per cache-read token (USD). Can be a number, a record of model name substrings to rates,
  // or null if the provider does not surface cache-read tokens or has no known caching discount.
  cacheSavingsPerToken: number | Record<string, number> | null
  badgeStyle: { color: string; background: string }
}

export const PROVIDERS: ProviderConfig[] = [
  {
    key: 'anthropic',
    displayName: 'Anthropic',
    colorVar: 'var(--provider-anthropic)',
    cacheSavingsPerToken: {
      'opus': 0.0000135,       // $13.50 per 1M ($15.00 - $1.50 input vs cache read rate)
      'haiku-3-5': 0.00000072, // $0.72 per 1M ($0.80 - $0.08 input vs cache read rate)
      'haiku-3': 0.00000022,   // $0.22 per 1M ($0.25 - $0.03 input vs cache read rate)
      'claude-3-haiku': 0.00000022,
      'haiku': 0.00000072,
      'sonnet': 0.0000027,     // $2.70 per 1M ($3.00 - $0.30 input vs cache read rate)
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
]

/** Stable display order for provider filter chips and dropdowns. */
export const PROVIDER_ORDER: string[] = PROVIDERS.map(p => p.key)

export function getProvider(key: string): ProviderConfig | undefined {
  return PROVIDERS.find(p => p.key === key)
}
