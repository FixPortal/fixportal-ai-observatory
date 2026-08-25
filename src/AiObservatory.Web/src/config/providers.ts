// Single source of truth for all provider metadata.
// Add new providers here — consuming modules derive from this list automatically.

export const PROVIDER_KEYS = ['anthropic', 'copilot', 'google', 'openai', 'moonshot'] as const
export type ProviderKey = typeof PROVIDER_KEYS[number]

export interface ProviderConfig {
  key: ProviderKey
  displayName: string
  colorVar: string
  badgeStyle: { color: string; background: string }
}

export const PROVIDERS = [
  {
    key: 'anthropic',
    displayName: 'Anthropic',
    colorVar: 'var(--provider-anthropic)',
    badgeStyle: { color: 'var(--provider-anthropic)', background: 'rgba(124,58,237,.12)' },
  },
  {
    key: 'copilot',
    displayName: 'Copilot',
    colorVar: 'var(--provider-copilot)',
    badgeStyle: { color: 'var(--provider-copilot)', background: 'rgba(219,39,119,.12)' },
  },
  {
    key: 'google',
    displayName: 'Google',
    colorVar: 'var(--provider-google)',
    badgeStyle: { color: 'var(--provider-google)', background: 'rgba(2,132,199,.12)' },
  },
  {
    key: 'openai',
    displayName: 'OpenAI',
    colorVar: 'var(--provider-openai)',
    badgeStyle: { color: 'var(--provider-openai)', background: 'rgba(234,88,12,.12)' },
  },
  {
    key: 'moonshot',
    displayName: 'Moonshot',
    colorVar: 'var(--provider-moonshot)',
    badgeStyle: { color: 'var(--provider-moonshot)', background: 'rgba(101,163,13,.12)' },
  },
] satisfies ProviderConfig[]

/** Stable display order for provider filter chips and dropdowns. */
export const PROVIDER_ORDER: ProviderKey[] = PROVIDERS.map(p => p.key)

export function getProvider(key: string): ProviderConfig | undefined {
  return PROVIDERS.find(p => p.key === key)
}
