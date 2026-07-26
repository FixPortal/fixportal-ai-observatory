import { describe, test, expect, vi, beforeEach, afterEach } from 'vitest'

const msalMock = vi.hoisted(() => ({
  getAccessToken: vi.fn(),
  apiKey: '',
  urlApiKey: '',
}))
vi.mock('../auth/msal', () => msalMock)

import {
  getAggregates, ApiError,
  createSpendCategory, patchSpendCategory, createSpendVendor, patchSpendVendor,
} from './client'

function mockFetchOnce(status: number, body: unknown = []) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

// Catalog writes get a plain-text body on failure (Results.BadRequest("..."),
// Results.Conflict("...")) unlike the JSON-only successes elsewhere, so this mock
// carries both json() and text().
function mockFetchOnceWithText(status: number, body: unknown, text = '') {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(text),
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

  test('surfaces the response body text instead of a generic status line', async () => {
    mockFetchOnceWithText(409, null, 'Category key already exists: credits')

    const error = await createSpendCategory(
      { key: 'credits', displayName: 'Credits', colorVar: null, sortOrder: 0 },
    ).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).message).toBe('Category key already exists: credits')
  })

  test('falls back to a generic status line when the body is empty', async () => {
    mockFetchOnceWithText(404, null, '')

    const error = await patchSpendCategory('missing-id', { archived: true }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).message).toMatch(/failed: 404/)
  })
})

describe('spend catalog writes', () => {
  test('creates a category and returns the created entity', async () => {
    const created = { id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '', sortOrder: 0, archivedAt: null }
    const fetchMock = mockFetchOnceWithText(201, created)

    const result = await createSpendCategory({ key: 'credits', displayName: 'Credits', colorVar: null, sortOrder: 0 })

    expect(result).toEqual(created)
    const [, init] = fetchMock.mock.calls[0]
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ key: 'credits', displayName: 'Credits', colorVar: null, sortOrder: 0 })
  })

  test('patches a category with only the changed field', async () => {
    const patched = { id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '', sortOrder: 0, archivedAt: '2026-07-26T00:00:00Z' }
    const fetchMock = mockFetchOnceWithText(200, patched)

    const result = await patchSpendCategory('c1', { archived: true })

    expect(result).toEqual(patched)
    const [path, init] = fetchMock.mock.calls[0]
    expect(path).toContain('/spend/categories/c1')
    expect(JSON.parse(init.body as string)).toEqual({ archived: true })
  })

  test('creates a vendor with no provider by sending null, not an empty string', async () => {
    const created = { id: 'v1', key: 'gha', displayName: 'GitHub Actions', provider: null, defaultCategoryId: null, archivedAt: null }
    const fetchMock = mockFetchOnceWithText(201, created)

    const result = await createSpendVendor({ key: 'gha', displayName: 'GitHub Actions', provider: null, defaultCategoryId: null })

    expect(result).toEqual(created)
    const [, init] = fetchMock.mock.calls[0]
    expect(JSON.parse(init.body as string).provider).toBeNull()
  })

  test('patches a vendor display name (rename)', async () => {
    const patched = { id: 'v1', key: 'anthropic', displayName: 'Anthropic Inc', provider: 'anthropic', defaultCategoryId: null, archivedAt: null }
    const fetchMock = mockFetchOnceWithText(200, patched)

    const result = await patchSpendVendor('v1', { displayName: 'Anthropic Inc' })

    expect(result).toEqual(patched)
    const [path, init] = fetchMock.mock.calls[0]
    expect(path).toContain('/spend/vendors/v1')
    expect(JSON.parse(init.body as string)).toEqual({ displayName: 'Anthropic Inc' })
  })
})
