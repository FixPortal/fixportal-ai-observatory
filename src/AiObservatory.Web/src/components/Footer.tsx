import { BrandWordmark } from '../design/BrandWordmark'

// FixPortal footer, monikered for this site. Models the estate's dashboard
// footer convention (CI dashboard): shared wordmark, a per-site tagline, and
// external links to fixportal.org. No copyright line (carried only on the
// public marketing footers).
export default function Footer() {
  return (
    <footer className="site-footer">
      <div className="site-footer__band" aria-hidden="true">
        <BrandWordmark height={30} className="site-footer__wordmark" />
        <span className="site-footer__tagline">AI · USAGE · OBSERVATORY</span>
      </div>
      <div className="site-footer__attrib">
        Built by{' '}
        <a href="https://www.fixportal.org/about" target="_blank" rel="noopener noreferrer">Chris Dowling</a>
        {' · '}
        <a href="https://www.fixportal.org" target="_blank" rel="noopener noreferrer">fixportal.org</a>
        {' · '}
        <a href="https://github.com/FixPortal" target="_blank" rel="noopener noreferrer">GitHub</a>
      </div>
    </footer>
  )
}
