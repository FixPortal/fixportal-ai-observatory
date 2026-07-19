import { describe, test, expect, vi, beforeEach, afterEach } from 'vitest'

const msalMock = vi.hoisted(() => ({
  getAccessToken: vi.fn(),
  apiKey: '',
  urlApiKey: '',
}))
vi.mock('../auth/msal', () => msalMock)

import { getAggregates, ApiError } from './client'

function mockFetchOnce(status: number, body: unknown = []) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

beforeEach(() => {
  msalMock.getAccessToken.mockReset().mockResolvedValue(null)
  msalMock.apiKey = ''
  msalMock.urlApiKey = ''
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('authHeaders precedence: Entra > URL viewer key > self-host key > none', () => {
  test('uses the Entra bearer token when available, even if a viewer/self-host key is also set', async () => {
    msalMock.getAccessToken.mockResolvedValue('entra-token')
    msalMock.urlApiKey = 'viewer-key'
    msalMock.apiKey = 'self-host-key'
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers.Authorization).toBe('Bearer entra-token')
    expect(headers['X-Observatory-Key']).toBeUndefined()
  })

  test('falls back to the URL viewer key when there is no Entra token', async () => {
    msalMock.getAccessToken.mockResolvedValue(null)
    msalMock.urlApiKey = 'viewer-key'
    msalMock.apiKey = 'self-host-key'
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers['X-Observatory-Key']).toBe('viewer-key')
    expect(headers.Authorization).toBeUndefined()
  })

  test('falls back to the self-host VITE_API_KEY when there is no Entra token and no viewer key', async () => {
    msalMock.getAccessToken.mockResolvedValue(null)
    msalMock.urlApiKey = ''
    msalMock.apiKey = 'self-host-key'
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers['X-Observatory-Key']).toBe('self-host-key')
  })

  test('sends no auth header at all when nothing is configured (local dev)', async () => {
    const fetchMock = mockFetchOnce(200)

    await getAggregates()

    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>
    expect(headers.Authorization).toBeUndefined()
    expect(headers['X-Observatory-Key']).toBeUndefined()
  })
})

describe('ApiError', () => {
  test('carries the HTTP status so the UI can tell auth failures from outages', async () => {
    mockFetchOnce(401)

    const error = await getAggregates().catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(401)
  })

  test('distinguishes a 403 from a 5xx by status code', async () => {
    mockFetchOnce(500)

    const error = await getAggregates().catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(500)
  })

  test('a rejected fetch (network outage) propagates rather than being swallowed', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(getAggregates()).rejects.toThrow('Failed to fetch')
  })
})
