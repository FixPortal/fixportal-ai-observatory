import { useRef, type KeyboardEvent } from 'react'

export type ThemeMode = 'light' | 'dark' | 'system'

interface ThemeToggleProps {
  value: ThemeMode
  onChange: (mode: ThemeMode) => void
}

const OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
  { value: 'system', label: 'System' },
]

export function ThemeToggle({ value, onChange }: ThemeToggleProps) {
  const btnRefs = useRef<(HTMLButtonElement | null)[]>([])

  // Radiogroup keyboard semantics: arrow keys move selection AND focus between the
  // non-selected options (which carry tabIndex=-1), so the control is fully keyboard
  // operable, not just clickable.
  const handleKeyDown = (e: KeyboardEvent, index: number) => {
    let next: number | null = null
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = (index + 1) % OPTIONS.length
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = (index - 1 + OPTIONS.length) % OPTIONS.length
    if (next !== null) {
      e.preventDefault()
      onChange(OPTIONS[next].value)
      btnRefs.current[next]?.focus()
    }
  }

  return (
    <div role="radiogroup" aria-label="Theme" className="fpds-theme-toggle">
      {OPTIONS.map((opt, i) => (
        <button
          key={opt.value}
          ref={el => { btnRefs.current[i] = el }}
          type="button"
          role="radio"
          aria-checked={value === opt.value}
          tabIndex={value === opt.value ? 0 : -1}
          onClick={() => onChange(opt.value)}
          onKeyDown={e => handleKeyDown(e, i)}
          className="fpds-theme-toggle__btn"
        >
          {opt.label}
        </button>
      ))}
    </div>
  )
}
