import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import SpendEntryModal from './SpendEntryModal'
import * as client from '../api/client'

const categories = [{ id: 'c1', key: 'credits', displayName: 'Credits', colorVar: '--c', sortOrder: 1, archivedAt: null }]
const vendors = [{ id: 'v1', key: 'anthropic', displayName: 'Anthropic', provider: 'anthropic', defaultCategoryId: 'c1', archivedAt: null }]

function renderModal(onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <SpendEntryModal categories={categories} vendors={vendors} onClose={onClose} />
    </QueryClientProvider>,
  )
}

describe('SpendEntryModal', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('posts an array of one, with source manual and no entry key', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: 'e1', status: 'created', reason: null }])
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(post).toHaveBeenCalledTimes(1))
    const [payload] = post.mock.calls[0]
    expect(payload).toHaveLength(1)
    expect(payload[0].source).toBe('manual')
    expect(payload[0].entryKey).toBeNull()
  })

  it('refuses to submit a negative amount', async () => {
    const post = vi.spyOn(client, 'postSpendEntries')
    renderModal()

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '-5' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
    expect(post).not.toHaveBeenCalled()
  })

  it('surfaces a rejected verdict instead of closing', async () => {
    vi.spyOn(client, 'postSpendEntries')
      .mockResolvedValue([{ id: null, status: 'rejected', reason: 'Unknown VendorId' }])
    const onClose = vi.fn()
    renderModal(onClose)

    fireEvent.change(screen.getByLabelText(/amount/i), { target: { value: '80' } })
    fireEvent.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Unknown VendorId'))
    expect(onClose).not.toHaveBeenCalled()
  })
})
