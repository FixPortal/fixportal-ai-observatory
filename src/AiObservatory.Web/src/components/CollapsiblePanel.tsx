import { useState, type ReactNode } from 'react'

interface CollapsiblePanelProps {
  id: string
  title: string
  summary?: string
  children: ReactNode
}

export function CollapsiblePanel({ id, title, summary, children }: CollapsiblePanelProps) {
  const [open, setOpen] = useState(() => localStorage.getItem(`panel-${id}-expanded`) === 'true')
  const bodyId = `panel-${id}-body`

  function toggle() {
    setOpen(prev => {
      const next = !prev
      localStorage.setItem(`panel-${id}-expanded`, String(next))
      return next
    })
  }

  return (
    <div className="collapsible-panel">
      <button
        type="button"
        className="collapsible-panel__header"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={toggle}
      >
        <span className="collapsible-panel__dot" aria-hidden="true" />
        <span className="collapsible-panel__title">{title}</span>
        {summary !== undefined && (
          <span className="collapsible-panel__summary">{summary}</span>
        )}
        <span
          className={`collapsible-panel__chevron${open ? ' collapsible-panel__chevron--open' : ''}`}
          aria-hidden="true"
        >
          ▾
        </span>
      </button>
      <div
        id={bodyId}
        className={`collapsible-panel__body-outer${open ? ' collapsible-panel__body-outer--open' : ''}`}
        aria-hidden={!open}
      >
        <div className="collapsible-panel__body-inner">
          {children}
        </div>
      </div>
    </div>
  )
}
