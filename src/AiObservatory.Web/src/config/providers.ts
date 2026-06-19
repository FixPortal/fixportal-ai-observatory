// Single source of truth for all provider metadata.
// Add new providers here — consuming modules derive from this list automatically.

export interface ProviderConfig {
  key: string
  displayName: string
  colorVar: string
  // Estimated savings per cache-read token (USD). Null if the provider does not
  // surface cache-read tokens or has no known caching discount.
  cacheSavingsPerToken: number | null
  badgeStyle: { color: string; background: string }
}

export const PROVIDERS: ProviderConfig[] = [
  {
    key: 'anthropic',
    displayName: 'Anthropic',
    colorVar: 'var(--provider-anthropic)',
    cacheSavingsPerToken: 0.0000027,   // $2.70 per 1M (full input rate minus cache read rate, Sonnet basis)
    badgeStyle: { color: 'var(--provider-anthropic)', background: 'rgba(124,58,237,.12)' },
  },
  {
    key: 'copilot',
    displayName: 'Copilot',
    colorVar: 'var(--provider-copilot)',
    cacheSavingsPerToken: null,
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
