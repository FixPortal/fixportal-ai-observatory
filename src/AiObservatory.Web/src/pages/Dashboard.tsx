import { lazy, Suspense, useState } from 'react'
import SummaryCards from '../components/SummaryCards'
import ModelBreakdown from '../components/ModelBreakdown'
import InsightsFeed from '../components/InsightsFeed'
import SubscriptionPanel from '../components/SubscriptionPanel'
import AdversarialReviewPanel from '../components/AdversarialReviewPanel'
import CavemanStatsPanel from '../components/CavemanStatsPanel'
import ReportingPage from './ReportingPage'
import Footer from '../components/Footer'
import { BrandWordmark } from '../design/BrandWordmark'
import { ThemeToggle } from '../design/ThemeToggle'
import { useDashboardStatus } from '../api/queries'
import { ApiError } from '../api/client'
import { authEnabled, signIn } from '../auth/msal'
import { useTheme } from '../theme/useTheme'

type DashboardTab = 'overview' | 'adversarial-review' | 'reporting'

// recharts is heavy and the charts sit below the fold — code-split them so the
// initial payload (nav + summary cards) paints without the chart library.
const SpendChart = lazy(() => import('../components/SpendChart'))
const ProviderSplit = lazy(() => import('../components/ProviderSplit'))

function ErrorBanner({ error }: { error: unknown }) {
  const isAuthError = error instanceof ApiError && (error.status === 401 || error.status === 403)
  if (isAuthError) {
    return (
      <div className="error-banner" role="alert">
        Your session has expired or you’re not authorised.{' '}
        {authEnabled && (
          <button type="button" className="error-banner__action" onClick={signIn}>
            Sign in again
          </button>
        )}
      </div>
    )
  }
  return (
    <div className="error-banner" role="alert">
      Couldn’t reach the API — data may be unavailable. Check the API service and try refreshing.
    </div>
  )
}

export default function Dashboard() {
  const { isError, isLoading, error } = useDashboardStatus()
  const { mode, setMode } = useTheme()
  const [tab, setTab] = useState<DashboardTab>('overview')
  return (
    <div className="dashboard">
      <header className="app-header">
        <span className="app-header__lockup">
          <BrandWordmark height={48} className="app-header__wordmark" />
          <span className="app-header__descriptor">AI Observatory</span>
        </span>
        <ThemeToggle value={mode} onChange={setMode} />
      </header>
      <nav className="page-nav" aria-label="Dashboard sections">
        <button
          type="button"
          className={`page-nav__tab${tab === 'overview' ? ' page-nav__tab--active' : ''}`}
          onClick={() => setTab('overview')}
        >
          Overview
        </button>
        <button
          type="button"
          className={`page-nav__tab${tab === 'adversarial-review' ? ' page-nav__tab--active' : ''}`}
          onClick={() => setTab('adversarial-review')}
        >
          Adversarial Review
        </button>
        <button
          type="button"
          className={`page-nav__tab${tab === 'reporting' ? ' page-nav__tab--active' : ''}`}
          onClick={() => setTab('reporting')}
        >
          Reporting
        </button>
      </nav>
      <main className="dashboard__main">
        {isError && <ErrorBanner error={error} />}
        {!isError && isLoading && (
          <output className="loading-banner" aria-live="polite">
            <span className="loading-banner__spinner" aria-hidden="true" />
            Loading data — the API may take a moment to wake up on first load…
          </output>
        )}
        {tab === 'overview' && (
          <>
            <SummaryCards />
            <div className="collapsible-panel-zone">
              <CavemanStatsPanel />
            </div>
            <SubscriptionPanel />
            <div className="main-grid">
              <div className="panel">
                <div className="panel-title">Daily spend — last 31 days</div>
                <Suspense fallback={<div className="chart-skeleton" />}>
                  <SpendChart />
                </Suspense>
              </div>
              <div className="panel">
                <div className="panel-title">Provider split</div>
                <Suspense fallback={<div className="chart-skeleton" />}>
                  <ProviderSplit />
                </Suspense>
              </div>
            </div>
            <div className="bottom-grid">
              <div className="panel">
                <div className="panel-title">Model breakdown</div>
                <ModelBreakdown />
              </div>
              <div className="panel">
                <div className="panel-title">Insights</div>
                <InsightsFeed />
              </div>
            </div>
          </>
        )}
        {tab === 'adversarial-review' && <AdversarialReviewPanel />}
        {tab === 'reporting' && <ReportingPage />}
      </main>
      <Footer />
    </div>
  )
}
