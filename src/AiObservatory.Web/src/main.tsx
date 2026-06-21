import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'
import './index.css'
import App from './App.tsx'
import { ThemeProvider } from './theme/ThemeProvider'
import { msalInstance } from './auth/msal'

const tree = (
  <StrictMode>
    <ThemeProvider>
      <App />
    </ThemeProvider>
  </StrictMode>
)

async function bootstrap() {
  const root = createRoot(document.getElementById('root')!)
  if (msalInstance) {
    // MSAL v3 requires initialize() before any other call, and the redirect
    // response must be drained before render so the account is in the cache.
    await msalInstance.initialize()
    const result = await msalInstance.handleRedirectPromise()
    if (result?.account) msalInstance.setActiveAccount(result.account)
    root.render(<MsalProvider instance={msalInstance}>{tree}</MsalProvider>)
  } else {
    root.render(tree)
  }
}

void bootstrap()
