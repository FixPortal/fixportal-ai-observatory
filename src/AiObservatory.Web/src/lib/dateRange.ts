import { useState, useCallback } from 'react'

type Preset = 7 | 31 | 90

function presetRange(days: Preset): { from: Date; to: Date } {
  const to = new Date()
  const from = new Date(to.getTime() - (days - 1) * 24 * 60 * 60 * 1000)
  return { from, to }
}

export function useDateRange() {
  const [preset, setPresetState] = useState<Preset | 'custom'>(31)
  const initial = presetRange(31)
  const [from, setFrom] = useState<Date>(initial.from)
  const [to, setTo] = useState<Date>(initial.to)

  const setPreset = useCallback((days: Preset) => {
    const range = presetRange(days)
    setPresetState(days)
    setFrom(range.from)
    setTo(range.to)
  }, [])

  const setCustom = useCallback((f: Date, t: Date) => {
    setPresetState('custom')
    setFrom(f)
    setTo(t)
  }, [])

  return { from, to, preset, setPreset, setCustom }
}
