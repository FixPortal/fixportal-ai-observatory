import { useMemo } from 'react'
import type { ProjectActivity } from '../api/client'
import { buildTreemapBlocks } from './treemapBlocks'
import { formatActiveTime } from '../lib/duration'

// Fixed palette cycled by index — projects are arbitrary strings, unlike
// providerColor's known provider set, so there's no semantic color to key off.
const PALETTE = ['var(--brand)', '#5a9c7c', '#3f6f8f', '#7a5fa0', '#b8895a', '#4f8a8b', '#9c5a7c', '#6b8e4e']
// Neutral fill for the overflow bucket so a 9th block doesn't wrap back to the brand
// colour (PALETTE[8 % 8]) and read as the largest project.
const OTHER_COLOR = 'var(--provider-other, #64748b)'

interface Props {
  projects: ProjectActivity[]
  selectedProject: string | null
  onSelectProject: (project: string | null) => void
}

export default function ProjectTreemap({ projects, selectedProject, onSelectProject }: Props) {
  const blocks = useMemo(() => buildTreemapBlocks(projects), [projects])

  if (blocks.length === 0) return <p className="panel-empty">No activity data for this period.</p>

  return (
    <div className="activity-treemap">
      {blocks.map((b, i) => (
        <button
          key={b.isOther ? '__other__' : b.project}
          type="button"
          className={`activity-treemap__block${!b.isOther && b.project === selectedProject ? ' activity-treemap__block--selected' : ''}`}
          style={{ flexGrow: b.activeSeconds, background: b.isOther ? OTHER_COLOR : PALETTE[i % PALETTE.length] }}
          // The "Other" bucket aggregates everything past the top N — there's no
          // single project to filter the table to, so it's not interactive.
          disabled={b.isOther}
          onClick={() => onSelectProject(b.project === selectedProject ? null : b.project)}
          title={`${b.project} — ${formatActiveTime(b.activeSeconds)} (${b.percent}%)`}
        >
          <span className="activity-treemap__label">{b.project}</span>
          <span className="activity-treemap__value">{formatActiveTime(b.activeSeconds)}</span>
        </button>
      ))}
    </div>
  )
}
