import { PROVIDERS } from '../config/providers'

// Maps a provider key to its categorical CSS-var colour (defined in index.css,
// re-themed per [data-theme]). Used for recharts fills. Unknown -> --provider-other.
const PROVIDER_VARS: Record<string, string> = Object.fromEntries(
  PROVIDERS.map(p => [p.key, p.colorVar])
)

export function providerColor(provider: string): string {
  return PROVIDER_VARS[provider] ?? 'var(--provider-other)'
}

// The Opus judge gets its own categorical tone; reviewers use their vendor color.
export function participantColor(reviewer: string, role: string): string {
  return role === 'judge' ? 'var(--provider-judge)' : providerColor(reviewer)
}
