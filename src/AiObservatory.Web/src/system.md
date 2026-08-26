# AI Observatory visual system

Canonical base: `@fixportal/design` 0.8.1 at tag `v0.8.1` / commit `6b3e3e0`, vendored from the FixPortal assets repository so public installs require no private package token.

## Product signature

AI Observatory is evidence-first: values carry source, scope, basis, freshness, and observation time. Billed GBP, estimates, and subscription notional value never share a total or visual claim.

## App-local palettes

Provider colours identify providers in charts, swatches, and provider badges only. Project colours identify project series only. Neither palette communicates status, selection, progress, or interaction. The canonical blue accent is interaction; green, amber, and red are status.

## Conformance

Use canonical surface, text, brand, status, spacing, radius, typography, focus, and motion rules. Version 0.8.1 supplies dedicated header/footer surfaces, brand contrast/background/ring roles, the 4/6/8 px radius ladder, and the 80 ms interaction duration; app CSS aliases those roles rather than restating their values. Borders provide depth; shadows are reserved for floating surfaces. Monospace is for values, identifiers, timestamps, and machine evidence.

Dashboard navigation retains its app-local roving tablist because it supports ArrowUp/ArrowDown aliases in addition to horizontal arrow navigation. Canonical 0.8.1 `Tabs` omits those existing keys, so adopting it would lose keyboard behaviour; its unused source is not vendored.
