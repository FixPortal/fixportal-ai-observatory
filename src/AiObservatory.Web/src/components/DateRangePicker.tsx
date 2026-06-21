import { useEffect, useRef, useState } from 'react'

interface Props {
  from: Date
  to: Date
  preset: 7 | 31 | 90 | 'custom'
  onPreset: (days: 7 | 31 | 90) => void
  onCustom: (from: Date, to: Date) => void
}

export default function DateRangePicker({ from, to, preset, onPreset, onCustom }: Props) {
  const [popoverOpen, setPopoverOpen] = useState(false)
  const [fromStr, setFromStr] = useState(from.toISOString().slice(0, 10))
  const [toStr, setToStr] = useState(to.toISOString().slice(0, 10))
  const popoverRef = useRef<HTMLDivElement>(null)
  const containerRef = useRef<HTMLDivElement>(null)

  // Sync input values when props change (e.g. when switching back to preset then custom)
  useEffect(() => {
    setFromStr(from.toISOString().slice(0, 10))
    setToStr(to.toISOString().slice(0, 10))
  }, [from, to])

  useEffect(() => {
    if (!popoverOpen) return

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') setPopoverOpen(false)
    }

    function onMouseDown(e: MouseEvent) {
      if (
        popoverRef.current &&
        !popoverRef.current.contains(e.target as Node) &&
        containerRef.current &&
        !containerRef.current.contains(e.target as Node)
      ) {
        setPopoverOpen(false)
      }
    }

    document.addEventListener('keydown', onKeyDown)
    document.addEventListener('mousedown', onMouseDown)
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.removeEventListener('mousedown', onMouseDown)
    }
  }, [popoverOpen])

  function handleApply() {
    const f = new Date(fromStr)
    const t = new Date(toStr)
    if (!isNaN(f.getTime()) && !isNaN(t.getTime())) {
      onCustom(f, t)
      setPopoverOpen(false)
    }
  }

  const PRESETS = [7, 31, 90] as const

  return (
    <div ref={containerRef} style={{ position: 'relative', display: 'inline-flex', alignItems: 'center', gap: 'var(--space-1)' }}>
      <div className="chart-toggle">
        {PRESETS.map(days => (
          <button
            key={days}
            type="button"
            className={`chart-toggle-btn${preset === days ? ' chart-toggle-btn--active' : ''}`}
            onClick={() => { setPopoverOpen(false); onPreset(days) }}
          >
            {days}d
          </button>
        ))}
        <button
          type="button"
          className={`chart-toggle-btn${preset === 'custom' ? ' chart-toggle-btn--active' : ''}`}
          onClick={() => setPopoverOpen(v => !v)}
        >
          Custom
        </button>
      </div>

      {popoverOpen && (
        <div
          ref={popoverRef}
          style={{
            position: 'absolute',
            top: 'calc(100% + 6px)',
            left: 0,
            zIndex: 20,
            background: 'var(--card-bg)',
            border: '1px solid var(--border)',
            borderRadius: 'var(--r-panel)',
            padding: 'var(--space-3)',
            display: 'flex',
            flexDirection: 'column',
            gap: 'var(--space-2)',
            minWidth: '220px',
            boxShadow: '0 8px 24px rgba(0, 0, 0, 0.35)',
          }}
        >
          <label style={{ fontSize: '0.7rem', color: 'var(--text-muted)', display: 'flex', flexDirection: 'column', gap: '2px' }}>
            From
            <input
              type="date"
              value={fromStr}
              onChange={e => setFromStr(e.target.value)}
              style={{
                fontFamily: 'var(--font-mono)',
                fontSize: '0.8rem',
                padding: '2px var(--space-2)',
                background: 'var(--app-bg)',
                color: 'var(--text)',
                border: '1px solid var(--border)',
                borderRadius: 'var(--r-control)',
              }}
            />
          </label>
          <label style={{ fontSize: '0.7rem', color: 'var(--text-muted)', display: 'flex', flexDirection: 'column', gap: '2px' }}>
            To
            <input
              type="date"
              value={toStr}
              onChange={e => setToStr(e.target.value)}
              style={{
                fontFamily: 'var(--font-mono)',
                fontSize: '0.8rem',
                padding: '2px var(--space-2)',
                background: 'var(--app-bg)',
                color: 'var(--text)',
                border: '1px solid var(--border)',
                borderRadius: 'var(--r-control)',
              }}
            />
          </label>
          <button
            type="button"
            onClick={handleApply}
            style={{
              marginTop: 'var(--space-1)',
              padding: 'var(--space-1) var(--space-3)',
              background: 'var(--brand)',
              color: 'var(--text-on-brand)',
              border: 'none',
              borderRadius: 'var(--r-control)',
              fontFamily: 'var(--font-sans)',
              fontSize: '0.75rem',
              fontWeight: 600,
              cursor: 'pointer',
            }}
          >
            Apply
          </button>
        </div>
      )}
    </div>
  )
}
