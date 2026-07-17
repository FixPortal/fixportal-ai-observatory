import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useDateRange } from './dateRange'

describe('useDateRange', () => {
  const FIXED = new Date('2026-06-21T12:00:00Z')
  beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(FIXED) })
  afterEach(() => { vi.useRealTimers() })

  it('defaults to 31-day preset', () => {
    const { result } = renderHook(() => useDateRange())
    expect(result.current.preset).toBe(31)
    const { to, from } = result.current
    const diffDays = Math.round((to.getTime() - from.getTime()) / 86400000)
    expect(diffDays).toBe(30) // 31 days inclusive = 30 day difference
  })

  it('setPreset(7) sets a 7-day window', () => {
    const { result } = renderHook(() => useDateRange())
    act(() => result.current.setPreset(7))
    expect(result.current.preset).toBe(7)
    const diffDays = Math.round((result.current.to.getTime() - result.current.from.getTime()) / 86400000)
    expect(diffDays).toBe(6)
  })

  it('setCustom changes to custom preset', () => {
    const { result } = renderHook(() => useDateRange())
    const from = new Date('2026-05-01')
    const to = new Date('2026-05-31')
    act(() => result.current.setCustom(from, to))
    expect(result.current.preset).toBe('custom')
    expect(result.current.from).toEqual(from)
    expect(result.current.to).toEqual(to)
  })
})
