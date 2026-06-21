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
  return (
    <div role="radiogroup" aria-label="Theme" className="fpds-theme-toggle">
      {OPTIONS.map(opt => (
        <button
          key={opt.value}
          type="button"
          role="radio"
          aria-checked={value === opt.value}
          tabIndex={value === opt.value ? 0 : -1}
          onClick={() => onChange(opt.value)}
          className="fpds-theme-toggle__btn"
        >
          {opt.label}
        </button>
      ))}
    </div>
  )
}
