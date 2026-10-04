import { useState } from 'react'

// Orange toggle switch cloned from settings.html .xf-switch CSS.
// Controlled when `checked` is provided, uncontrolled otherwise.
export default function ToggleSwitch({
  checked,
  defaultChecked = false,
  onChange,
  label,
}: {
  checked?: boolean
  defaultChecked?: boolean
  onChange?: (checked: boolean) => void
  label: string
}) {
  const [internal, setInternal] = useState(defaultChecked)
  const on = checked ?? internal
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      aria-label={label}
      data-on={on ? '1' : '0'}
      className="xf-switch"
      onClick={(e) => {
        e.preventDefault()
        const next = !on
        if (checked === undefined) setInternal(next)
        onChange?.(next)
      }}
    >
      <span className="xf-knob" />
    </button>
  )
}
