import { test, expect } from 'vitest'
import { formatActiveTime } from './duration'

test('formats under an hour as minutes only', () => {
  expect(formatActiveTime(45 * 60)).toBe('45m')
})

test('formats over an hour as hours and minutes', () => {
  expect(formatActiveTime(6 * 3600 + 40 * 60)).toBe('6h 40m')
})

test('rounds to the nearest minute', () => {
  expect(formatActiveTime(89)).toBe('1m') // 89s rounds to 1m, not 0m
})

test('formats zero seconds as 0m', () => {
  expect(formatActiveTime(0)).toBe('0m')
})

test('formats an exact hour with no leftover minutes', () => {
  expect(formatActiveTime(3600)).toBe('1h 0m')
})
