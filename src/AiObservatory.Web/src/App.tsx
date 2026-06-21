import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import Dashboard from './pages/Dashboard'
import AuthGate from './auth/AuthGate'

// Module-scoped so a StrictMode double-invoke or remount never discards the cache.
const queryClient = new QueryClient()

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthGate>
        <Dashboard />
      </AuthGate>
    </QueryClientProvider>
  )
}
