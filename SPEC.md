# TNX Options Profile — Specification

**Target:** Quantower v1.146.7, .NET 10, `TNXIndicators` namespace, GDI+ `OnPaintChart`.

## Overview

A docked options profile painted as an opaque overlay column on the right (or left) edge of the chart. Bars split from a centre line: calls one way, puts the other. Caption at the bottom, summary at the top.

## Layout

- Panel width: `clamp(rect.Width * WidthPct/100, 90, rect.Width * 0.60)`
- Panel background: opaque fill at `PanelAlpha`
- Divider line at panel edge
- Centre line splits call/put bars
- Strike ruler on the chart side of the panel

## Data Sources

| Source | Meaning |
|---|---|
| GEX (call − put) | Gamma-weighted, dealer-signed |
| GEX split | Call GEX and put GEX side by side |
| C/P Sigma | Raw traded contracts |
| C Sigma − P Sigma | Net traded contracts |
| C/P open interest | OI split |
| C OI − P OI | Net OI |
| DEX | Delta exposure |

## Formulas

**Black-76 gamma:** gamma = e^(−rT) · φ(d1) / (F · σ · √T)


**GEX per strike:** GEX$ = gamma · OI · multiplier · F² · 0.01

**Max pain:**  pain(K) = Σ callOI·max(0, K−Kᵢ) + Σ putOI·max(0, Kᵢ−K)

**Expected move:** EM = F · IV_atm · √T



## Bucketing

Anchored price-grid bucketing. Bucket membership depends only on price, never on viewport.

- `step` = smallest positive gap between adjacent strikes
- `anchor` = `floor(levels[0] / step) * step` (fixed)
- `merge` = `NiceCeil(MinPitch / pxPerStep)`
- `bucketPrice` = `merge * step`

Rows tile exactly: row k's bottom == row k+1's top.

## Settings (defaults)

**Data:** Source = GEX (call−put) · Filter = 0DTE · StrikesPerSide = 12 · Weight = OpenInterest · Dealer = LongCallShortPut · Rate = 4.25% · Mode = LiveQuotes

**Layout:** Side = Right · WidthPct = 12 · PanelAlpha = 0 · BarLenPct = 88 · MinPitch = 3 · BarGap = 1

**Bars:** Fill = Solid · BarOpacity = 100 · BorderWidth = 1.0 · TickHeightPct = 88 · ValueLabels = true · Scale = Sqrt

**Peaks:** CallPeak/PutPeak enabled, highlight bar, line, Dash, width 1

**Levels:** ZeroGamma = on · MaxPain = on · ExpMove = on · Sigma2 = 2.0 · LevelLabels = on · Curve = on

**Theme:** DeepCharts

## Acceptance Criteria

1. No overlap, no gaps between rows at any zoom
2. Bucket membership stable under pan
3. Label/bar agreement within 4px
4. Tick liveness: F-dependent numbers update within 150ms of tick
5. Frame budget: <500 µs, 0 bytes allocated per frame

