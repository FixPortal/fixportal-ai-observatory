// Entra (Azure AD) sign-in wiring. The whole module is a no-op unless
// VITE_AAD_CLIENT_ID is set at build time: local `npm run dev` ships no Entra
// config, so `msalInstance` is null, `authEnabled` is false, and the app talks to
// the (key-free) local API directly. In production the build bakes the client id,
// tenant, and API scope, and every request carries a bearer token.
import { PublicClientApplication, type Configuration, type RedirectRequest } from '@azure/msal-browser'

const clientId = import.meta.env.VITE_AAD_CLIENT_ID ?? ''
const tenantId = import.meta.env.VITE_AAD_TENANT_ID ?? ''
const apiScope = import.meta.env.VITE_AAD_API_SCOPE ?? ''

export const authEnabled = clientId.length > 0

/** Scopes requested for the API access token (and at interactive sign-in). */
export const loginRequest: RedirectRequest = { scopes: apiScope ? [apiScope] : [] }

const config: Configuration = {
  auth: {
    clientId,
    authority: `https://login.microsoftonline.com/${tenantId}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: { cacheLocation: 'localStorage' },
}

export const msalInstance = authEnabled ? new PublicClientApplication(config) : null

/**
 * Acquire an API access token for the signed-in account, refreshing silently.
 * Returns null when auth is disabled (dev) or no account is signed in. On a silent
 * failure (expired refresh token, consent revoked) it kicks off an interactive
 * redirect and returns null for this attempt — the page reloads post-redirect.
 */
/** Start an interactive sign-in (redirect). No-op when auth is disabled. */
export function signIn(): void {
  msalInstance?.loginRedirect(loginRequest).catch(() => { /* redirect aborted */ })
}

export async function getAccessToken(): Promise<string | null> {
  if (!msalInstance) return null
  const account = msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0]
  if (!account) return null
  try {
    const result = await msalInstance.acquireTokenSilent({ ...loginRequest, account })
    return result.accessToken
  } catch {
    await msalInstance.acquireTokenRedirect({ ...loginRequest, account })
    return null
  }
}

// Self-host mode: set VITE_API_KEY to authenticate the UI with the API key instead
// of Entra. AuthGate is a no-op (no sign-in screen); the key is injected as
// X-Observatory-Key on every request. Takes no effect when authEnabled is true.
export const apiKey = import.meta.env.VITE_API_KEY ?? ''

// Viewer-key share link: ?key=<value> in the URL grants read-only access without
// Entra sign-in. Read once at module load — the value stays in memory for the
// session; the param remains in the URL so bookmarks stay self-contained.
export const urlApiKey = new URLSearchParams(window.location.search).get('key') ?? ''
