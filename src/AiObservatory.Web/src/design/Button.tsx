import type React from 'react'

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'ghost'
  size?: 'sm' | 'md'
}

export function Button({ variant = 'primary', size = 'md', className, ...rest }: ButtonProps) {
  const cls = ['fpds-btn', `fpds-btn--${variant}`, `fpds-btn--${size}`, className]
    .filter(Boolean)
    .join(' ')
  return <button type="button" className={cls} {...rest} />
}
