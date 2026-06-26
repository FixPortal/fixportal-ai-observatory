import { test, beforeEach, expect } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { CollapsiblePanel } from './CollapsiblePanel'

beforeEach(() => localStorage.clear())

test('collapsed by default when no localStorage entry', () => {
  render(
    <CollapsiblePanel id="test" title="Test panel">
      <p>body content</p>
    </CollapsiblePanel>
  )
  expect(screen.getByRole('button')).toHaveAttribute('aria-expanded', 'false')
})

test('expanded when localStorage has true', () => {
  localStorage.setItem('panel-test-expanded', 'true')
  render(
    <CollapsiblePanel id="test" title="Test panel">
      <p>body content</p>
    </CollapsiblePanel>
  )
  expect(screen.getByRole('button')).toHaveAttribute('aria-expanded', 'true')
})

test('click toggles open state and writes localStorage', () => {
  render(
    <CollapsiblePanel id="test" title="Test panel">
      <p>body content</p>
    </CollapsiblePanel>
  )
  const btn = screen.getByRole('button')
  fireEvent.click(btn)
  expect(btn).toHaveAttribute('aria-expanded', 'true')
  expect(localStorage.getItem('panel-test-expanded')).toBe('true')
  fireEvent.click(btn)
  expect(btn).toHaveAttribute('aria-expanded', 'false')
  expect(localStorage.getItem('panel-test-expanded')).toBe('false')
})

test('aria-controls points to body element id', () => {
  render(
    <CollapsiblePanel id="test" title="Test panel">
      <p>body content</p>
    </CollapsiblePanel>
  )
  expect(screen.getByRole('button')).toHaveAttribute('aria-controls', 'panel-test-body')
  expect(document.getElementById('panel-test-body')).toBeInTheDocument()
})

test('summary shown in header when provided', () => {
  render(
    <CollapsiblePanel id="test" title="Test panel" summary="5 sessions · £20 saved">
      <p>body content</p>
    </CollapsiblePanel>
  )
  expect(screen.getByText('5 sessions · £20 saved')).toBeInTheDocument()
})

test('no summary span rendered when summary prop omitted', () => {
  render(
    <CollapsiblePanel id="test" title="Test panel">
      <p>body content</p>
    </CollapsiblePanel>
  )
  expect(screen.getByRole('button').querySelector('.collapsible-panel__summary')).toBeNull()
})
