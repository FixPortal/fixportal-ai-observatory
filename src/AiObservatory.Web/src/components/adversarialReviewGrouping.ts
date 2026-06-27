import type { AdversarialReviewRun } from '../api/client'

const REVIEWER_ORDER = ['anthropic', 'google', 'openai']

export interface RunGroup {
  runId: string
  repo: string | null
  recordedAt: string
  participants: AdversarialReviewRun[]
  totals: { raised: number; accepted: number; costUsd: number; durationMs: number }
  isComplete: boolean
  statusReason: string
}

export function formatDuration(ms: number): string {
  const totalSec = Math.round(ms / 1000)
  if (totalSec < 60) return `${totalSec}s`
  const m = Math.floor(totalSec / 60)
  const s = totalSec % 60
  return `${m}m${String(s).padStart(2, '0')}s`
}

function sortParticipants(a: AdversarialReviewRun, b: AdversarialReviewRun): number {
  const ra = a.role === 'judge' ? 99 : REVIEWER_ORDER.indexOf(a.reviewer)
  const rb = b.role === 'judge' ? 99 : REVIEWER_ORDER.indexOf(b.reviewer)
  return ra - rb
}

export function groupRuns(runs: AdversarialReviewRun[]): RunGroup[] {
  const byRun = new Map<string, AdversarialReviewRun[]>()
  for (const r of runs) {
    const list = byRun.get(r.runId) ?? []
    list.push(r)
    byRun.set(r.runId, list)
  }

  const groups: RunGroup[] = []
  for (const [runId, participants] of byRun) {
    participants.sort(sortParticipants)
    const reviewerVendors = new Set(participants.filter(p => p.role === 'reviewer').map(p => p.reviewer))
    const hasJudge = participants.some(p => p.role === 'judge')
    const reviewerCount = reviewerVendors.size
    const isComplete = reviewerCount >= 3 && hasJudge
    const statusReason = isComplete
      ? 'complete'
      : `${reviewerCount} of 3 reviewers${hasJudge ? '' : ' · no judge'}`

    groups.push({
      runId,
      repo: participants.find(p => p.repo)?.repo ?? null,
      recordedAt: participants[0].recordedAt,
      participants,
      totals: {
        raised: participants.reduce((s, p) => s + p.issuesRaised, 0),
        accepted: participants.reduce((s, p) => s + p.issuesAccepted, 0),
        costUsd: Math.round(participants.reduce((s, p) => s + p.costUsd, 0) * 1e6) / 1e6,
        durationMs: participants.reduce((s, p) => s + p.reviewDurationMs, 0),
      },
      isComplete,
      statusReason,
    })
  }

  return groups.sort((a, b) => b.recordedAt.localeCompare(a.recordedAt))
}
