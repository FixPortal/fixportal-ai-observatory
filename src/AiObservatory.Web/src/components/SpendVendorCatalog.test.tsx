import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import SpendVendorCatalog from './SpendVendorCatalog'
import * as client from '../api/client'

const categories = [
  { id: 'k1', key: 'compute', displayName: 'Compute', colorVar: '', sortOrder: 1, archivedAt: null },
  { id: 'k2', key: 'legacy', displayName: 'Legacy', colorVar: '', sortOrder: 2, archivedAt: '2026-06-01T00:00:00Z' },
]

const vendors = [
  { id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'k1', archivedAt: null },
  { id: 'v2', key: 'gha', displayName: 'GitHub Actions', provider: null, defaultCategoryId: null, archivedAt: '2026-06-01T00:00:00Z' },
]

function renderPanel() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendVendorCatalog vendors={vendors} categories={categories} />
    </QueryClientProvider>,
  )
}

describe('SpendVendorCatalog', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('lists both live and archived vendors, visibly distinguishing the archived one', () => {
    renderPanel()
    // "Anthropic" appears more than once here -- the vendor's own name, its
    // provider column (this fixture's vendor shares a name with its provider), and
    // the provider <select>'s own option -- so just prove the row rendered at all.
    expect(screen.getAllByText('Anthropic').length).toBeGreaterThanOrEqual(2)
    expect(screen.getByText('GitHub Actions')).toBeInTheDocument()
    expect(screen.getByText(/archived/i)).toBeInTheDocument()
  })

  it('shows an explicit "no provider" label for a vendor with no token estimate, not a blank cell', () => {
    renderPanel()
    const row = screen.getByText('GitHub Actions').closest('li')!
    expect(within(row).getByText(/no provider/i)).toBeInTheDocument()
  })

  it('offers only live categories as the default-category choice, not the archived one', () => {
    renderPanel()
    const select = screen.getByLabelText(/default category/i) as HTMLSelectElement
    const optionLabels = Array.from(select.options).map(o => o.textContent)
    expect(optionLabels).toContain('Compute')
    expect(optionLabels).not.toContain('Legacy')
  })

  it('creates a vendor with an explicitly chosen provider', async () => {
    const create = vi.spyOn(client, 'createSpendVendor').mockResolvedValue({
      id: 'v3', key: 'openai-direct', displayName: 'OpenAI Direct', provider: 'openai', defaultCategoryId: null, archivedAt: null,
    })
    renderPanel()

    fireEvent.change(screen.getByLabelText(/^key$/i), { target: { value: 'openai-direct' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'OpenAI Direct' } })
    fireEvent.change(screen.getByLabelText(/provider/i), { target: { value: 'openai' } })
    fireEvent.click(screen.getByRole('button', { name: /add vendor/i }))

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'openai-direct', displayName: 'OpenAI Direct', provider: 'openai' }),
    )
  })

  it('creates a vendor with no provider as an explicit null, the normal case for unmetered tools', async () => {
    const create = vi.spyOn(client, 'createSpendVendor').mockResolvedValue({
      id: 'v4', key: 'coderabbit', displayName: 'CodeRabbit', provider: null, defaultCategoryId: null, archivedAt: null,
    })
    renderPanel()

    fireEvent.change(screen.getByLabelText(/^key$/i), { target: { value: 'coderabbit' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'CodeRabbit' } })
    fireEvent.click(screen.getByRole('button', { name: /add vendor/i }))

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1))
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ provider: null }))
  })

  it('surfaces a 400 bad-slug failure from the server rather than a generic message', async () => {
    vi.spyOn(client, 'createSpendVendor').mockRejectedValue(
      new client.ApiError(400, 'Key must be a slug of 60 characters or fewer and DisplayName is required'),
    )
    renderPanel()

    fireEvent.change(screen.getByLabelText(/^key$/i), { target: { value: 'not a slug!' } })
    fireEvent.change(screen.getByLabelText(/display name/i), { target: { value: 'X' } })
    fireEvent.click(screen.getByRole('button', { name: /add vendor/i }))

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/slug of 60 characters/))
  })

  it('archives a live vendor', async () => {
    const patch = vi.spyOn(client, 'patchSpendVendor').mockResolvedValue({ ...vendors[0], archivedAt: '2026-07-26T00:00:00Z' })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^archive anthropic$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('v1', { archived: true }))
  })

  it('unarchives an archived vendor', async () => {
    const patch = vi.spyOn(client, 'patchSpendVendor').mockResolvedValue({ ...vendors[1], archivedAt: null })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^unarchive github actions$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('v2', { archived: false }))
  })

  it('renames a vendor via PATCH DisplayName', async () => {
    const patch = vi.spyOn(client, 'patchSpendVendor').mockResolvedValue({ ...vendors[0], displayName: 'Anthropic Inc' })
    renderPanel()

    fireEvent.click(screen.getByRole('button', { name: /^rename anthropic$/i }))
    const input = screen.getByLabelText(/rename anthropic/i)
    fireEvent.change(input, { target: { value: 'Anthropic Inc' } })
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }))

    await waitFor(() => expect(patch).toHaveBeenCalledWith('v1', { displayName: 'Anthropic Inc' }))
  })
})
