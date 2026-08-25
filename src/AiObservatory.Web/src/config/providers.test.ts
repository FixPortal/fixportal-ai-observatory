import { expect, test } from 'vitest'
import { PROVIDER_ORDER, getProvider } from './providers'
import { providerColor } from '../theme/providerColors'

// The backend's AiObservatory.Data.Entities.Provider enum. Any provider the API can
// persist must be renderable here — Moonshot shipped in the enum and in the adversarial
// review grouping but was missing from PROVIDERS, so its usage rows would have rendered
// as unnamed grey "other". Keep this list in step with the enum.
const BACKEND_PROVIDERS = ['anthropic', 'copilot', 'google', 'openai', 'moonshot']

test('every backend provider has a frontend config entry', () => {
  expect([...PROVIDER_ORDER].sort()).toEqual([...BACKEND_PROVIDERS].sort())
})

test.each(BACKEND_PROVIDERS)('%s resolves to its own colour, not the "other" fallback', key => {
  expect(providerColor(key)).toBe(`var(--provider-${key})`)
  expect(providerColor(key)).not.toBe('var(--provider-other)')
})

test.each(BACKEND_PROVIDERS)('%s declares a display name and badge style', key => {
  const provider = getProvider(key)
  expect(provider?.displayName).toBeTruthy()
  expect(provider?.badgeStyle.color).toBe(`var(--provider-${key})`)
})

test.each(BACKEND_PROVIDERS)('%s has no frontend cache-pricing rate', key => {
  expect(getProvider(key)).not.toHaveProperty('cacheSavingsPerToken')
})

test('an unknown provider still falls back to the neutral colour', () => {
  expect(providerColor('not-a-provider')).toBe('var(--provider-other)')
})

// Not covered here: that each --provider-<key> variable actually exists in index.css.
// Vitest stubs CSS imports, so `?raw` reads back empty, and node:fs would mean adding
// @types/node for a single assertion. A missing variable renders the series transparent
// rather than throwing — see the "three edits in step" note in the README.
