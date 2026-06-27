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

function participantRank(p: AdversarialReviewRun): number {
  if (p.role === 'judge') return 99
  const idx = REVIEWER_ORDER.indexOf(p.reviewer)
  // Unknown vendors sort after the known reviewers but before the judge,
  // rather than ahead of everything on indexOf's -1.
  return idx === -1 ? REVIEWER_ORDER.length : idx
}

function sortParticipants(a: AdversarialReviewRun, b: AdversarialReviewRun): number {
  return participantRank(a) - participantRank(b)
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
    // Earliest participant timestamp = the run's time. Derive it before sorting
    // so it never depends on participant display order (ISO-8601 sorts lexically).
    const recordedAt = participants.reduce(
      (earliest, p) => (p.recordedAt < earliest ? p.recordedAt : earliest),
      participants[0].recordedAt,
    )
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
      recordedAt,
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
